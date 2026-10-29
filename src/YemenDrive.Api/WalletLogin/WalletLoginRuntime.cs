using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;

namespace YemenDrive.Api.WalletLogin;

/// <summary>Owns a login client for each enabled payment record in this API process.</summary>
public sealed class WalletLoginRuntime(
    IServiceScopeFactory scopes,
    IWalletLoginClientFactory factory,
    WalletProviderCatalog catalog,
    WalletCredentialsProtector protector,
    ILogger<WalletLoginRuntime> logger) : IAsyncDisposable
{
    private sealed class Entry(Guid version)
    {
        public Guid Version { get; } = version;
        public IWalletLoginClient? Client { get; set; }
        public string Status { get; set; } = "LoggingIn";
    }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Entry> entries = new();

    public string? GetStatus(int methodId, Guid version) =>
        entries.TryGetValue(methodId, out var entry) && entry.Version == version ? entry.Status : null;

    public async Task<eqsWallets.WalletStepResult> WithPaymentClientAsync(int methodId, Guid version, string providerCode,
        Func<eqsWallets.WalletClient, Task<eqsWallets.WalletStepResult>> action, CancellationToken token)
    {
        // Keep the exact authenticated client alive throughout the call, even if credentials change.
        try { await gate.WaitAsync(token); }
        catch (OperationCanceledException) { throw new YemenDrive.Api.WalletPayments.WalletSessionUnavailableException(); }
        var dispatched = false;
        try
        {
            if (!YemenDrive.Api.WalletPayments.WalletPaymentProviders.IsSupported(providerCode) ||
                !entries.TryGetValue(methodId, out var entry) || entry.Version != version ||
                entry.Status != "Ready" || entry.Client is not WalletLoginClient client)
                throw new YemenDrive.Api.WalletPayments.WalletSessionUnavailableException();
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            if (!await db.PaymentProviderAccounts.AsNoTracking().AnyAsync(x => x.PaymentMethodId == methodId &&
                x.ConfigurationVersion == version && x.IsEnabled && x.ProviderCode == providerCode &&
                x.PaymentMethod.IsActive && x.PaymentMethod.IsAvailableForRidePayment && x.PaymentMethod.ProviderCode == providerCode &&
                x.PaymentMethod.Kind == YemenDrive.Database.Entities.PaymentMethodKind.ExternalWallet, token))
                throw new YemenDrive.Api.WalletPayments.WalletSessionUnavailableException();
            dispatched = true;
            return await action(client.Client);
        }
        catch (Exception) when (!dispatched) { throw new YemenDrive.Api.WalletPayments.WalletSessionUnavailableException(); }
        finally { gate.Release(); }
    }

    public async Task ReconcileAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            var accounts = await db.PaymentProviderAccounts.AsNoTracking()
                .Where(x => x.IsEnabled && x.PaymentMethod.IsActive &&
                    x.PaymentMethod.Kind == YemenDrive.Database.Entities.PaymentMethodKind.ExternalWallet &&
                    x.ProviderCode == x.PaymentMethod.ProviderCode)
                .ToListAsync(token);
            var current = accounts.ToDictionary(x => x.PaymentMethodId, x => x.ConfigurationVersion);
            foreach (var pair in entries.ToArray())
            {
                if (!current.TryGetValue(pair.Key, out var version) || version != pair.Value.Version)
                {
                    entries.TryRemove(pair.Key, out _);
                    await DisposeClientAsync(pair.Value.Client);
                }
            }

            foreach (var account in accounts)
            {
                if (entries.TryGetValue(account.PaymentMethodId, out var previous) &&
                    previous.Version == account.ConfigurationVersion) continue;

                var now = DateTime.UtcNow;
                var query = db.PaymentProviderAccounts.Where(x => x.Id == account.Id &&
                    x.ConfigurationVersion == account.ConfigurationVersion && x.IsEnabled && x.PaymentMethod.IsActive);
                var claimed = await query.ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.LoginStatus, "LoggingIn")
                    .SetProperty(x => x.LastAttemptAtUtc, (DateTime?)now)
                    .SetProperty(x => x.LastLoginAtUtc, (DateTime?)null)
                    .SetProperty(x => x.LastErrorCode, (string?)null), token);
                if (claimed == 0) continue;

                var entry = new Entry(account.ConfigurationVersion);
                entries[account.PaymentMethodId] = entry;
                string? error = null;
                try
                {
                    string json;
                    try { json = protector.Unprotect(account.ProviderCode, account.CredentialsEncrypted); }
                    catch (System.Security.Cryptography.CryptographicException)
                    { throw new WalletCredentialException(); }
                    var options = catalog.Parse(account.ProviderCode, json);
                    entry.Client = factory.Create();
                    var success = await entry.Client.LoginAsync(options, token);
                    entry.Status = success ? "Ready" : "Failed";
                    if (!success) error = "wallet_login_failed";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    entries.TryRemove(account.PaymentMethodId, out _);
                    await DisposeClientAsync(entry.Client);
                    throw;
                }
                catch (WalletCredentialException)
                { entry.Status = "Failed"; error = "wallet_credentials_unreadable"; }
                catch (YemenDrive.Shared.Api.ServiceException)
                { entry.Status = "Failed"; error = "wallet_options_invalid"; }
                catch (Exception)
                { entry.Status = "Failed"; error = "wallet_login_failed"; }

                if (entry.Status != "Ready")
                {
                    await DisposeClientAsync(entry.Client);
                    entry.Client = null;
                }
                var saved = await query.ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.LoginStatus, entry.Status)
                    .SetProperty(x => x.LastLoginAtUtc, entry.Status == "Ready" ? (DateTime?)DateTime.UtcNow : null)
                    .SetProperty(x => x.LastErrorCode, error), token);
                if (saved == 0)
                {
                    entries.TryRemove(account.PaymentMethodId, out _);
                    await DisposeClientAsync(entry.Client);
                }
                else
                {
                    // Provider exceptions/responses may contain credentials. Log only identifiers and safe codes.
                    logger.LogInformation("Wallet login for payment method {PaymentMethodId}: {Status}, {ErrorCode}",
                        account.PaymentMethodId, entry.Status, error);
                }
            }
        }
        finally { gate.Release(); }
    }

    private sealed class WalletCredentialException : Exception;

    private static async ValueTask DisposeClientAsync(IWalletLoginClient? client)
    {
        if (client is null) return;
        try { await client.DisposeAsync(); }
        catch (Exception) { /* Never log provider error payloads during cleanup. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            foreach (var entry in entries.Values) await DisposeClientAsync(entry.Client);
            entries.Clear();
        }
        finally { gate.Release(); }
    }
}
