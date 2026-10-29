using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using PaymentMethodEntity = YemenDrive.Database.Entities.PaymentMethod;

namespace YemenDrive.Api.WalletLogin;

public sealed class WalletLoginConfigurationService(
    WalletProviderCatalog catalog, WalletCredentialsProtector protector) : IWalletLoginConfiguration
{
    public async Task ConfigureAsync(YemenDriveDbContext db, PaymentMethodEntity method,
        PaymentMethodModel model, CancellationToken token)
    {
        if (method.Kind == PaymentMethodKind.ExternalWallet && catalog.Contains(method.ProviderCode))
            method.ProviderCode = catalog.Find(method.ProviderCode).Code;
        var account = method.Id == 0 ? null : await db.PaymentProviderAccounts
            .SingleOrDefaultAsync(x => x.PaymentMethodId == method.Id, token);
        var hasJson = !string.IsNullOrWhiteSpace(model.ProviderOptionsJson);
        var providerChanged = account is not null &&
            !string.Equals(account.ProviderCode, method.ProviderCode, StringComparison.OrdinalIgnoreCase);

        if (method.Kind != PaymentMethodKind.ExternalWallet)
        {
            if (hasJson || model.ProviderLoginEnabled == true)
                throw new ServiceException("wallet_provider_required", "إعدادات دخول المحفظة خاصة بطرق الدفع من نوع محفظة خارجية.");
            if (account is not null) db.PaymentProviderAccounts.Remove(account);
            return;
        }

        if (hasJson)
        {
            catalog.Parse(method.ProviderCode, model.ProviderOptionsJson!);
            hasJson = account is null || providerChanged || !HasSameCredentials(account, model.ProviderOptionsJson!);
            if (hasJson)
            {
                account ??= new PaymentProviderAccount { PaymentMethod = method, ProviderCode = method.ProviderCode };
                account.ProviderCode = method.ProviderCode;
                account.CredentialsEncrypted = protector.Protect(method.ProviderCode, model.ProviderOptionsJson!);
                if (db.Entry(account).State == EntityState.Detached) db.PaymentProviderAccounts.Add(account);
            }
        }
        else if (providerChanged)
        {
            if (model.ProviderLoginEnabled == true)
                throw new ServiceException("wallet_credentials_required", "أدخل بيانات دخول المزود الجديد قبل تفعيل تسجيل الدخول.");
            db.PaymentProviderAccounts.Remove(account!);
            return;
        }

        if (account is null)
        {
            if (model.ProviderLoginEnabled == true)
                throw new ServiceException("wallet_credentials_required", "أدخل إعدادات JSON للمحفظة قبل تفعيل تسجيل الدخول.");
            return;
        }

        account.ProviderCode = method.ProviderCode;

        var enabled = model.ProviderLoginEnabled ?? account.IsEnabled;
        if (hasJson || enabled != account.IsEnabled)
        {
            account.IsEnabled = enabled;
            account.ConfigurationVersion = Guid.NewGuid();
            account.LoginStatus = enabled ? "Pending" : "Disabled";
            account.LastAttemptAtUtc = null;
            account.LastLoginAtUtc = null;
            account.LastErrorCode = null;
            account.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private bool HasSameCredentials(PaymentProviderAccount account, string json)
    {
        try
        {
            var stored = protector.Unprotect(account.ProviderCode, account.CredentialsEncrypted);
            // Formatting and property order changes do not require a fresh provider session.
            return JsonNode.DeepEquals(JsonNode.Parse(stored), JsonNode.Parse(json));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            // A valid replacement can repair unreadable stored credentials without exposing their content.
            return false;
        }
    }
}
