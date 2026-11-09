using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api.Sms;

public sealed class SmsDeliveryWorker(IServiceScopeFactory scopes, SmsProviderRuntime runtime, SmsDeliveryService delivery,
    ILogger<SmsDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("تعذرت معالجة سجل SMS؛ ستتم المحاولة في الدورة التالية."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task TickAsync(CancellationToken token)
    {
        var connection = await runtime.ConnectAsync(token);
        if (!await runtime.IsConfiguredAsync(token)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var now = DateTime.UtcNow;
        var stale = now.AddMinutes(-2);
        await db.SmsDispatches.Where(x => x.State == SmsDispatchState.Sending && x.ClaimedAtUtc < stale)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, SmsDispatchState.Unconfirmed).SetProperty(x => x.ProtectedPayload, (string?)null)
                .SetProperty(x => x.ErrorCode, "sms_delivery_unconfirmed"), token);
        await db.SmsDispatches.Where(x => x.State == SmsDispatchState.Pending && x.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, SmsDispatchState.Expired).SetProperty(x => x.ProtectedPayload, (string?)null), token);
        if (!connection.Connected) return;
        var ids = await db.SmsDispatches.AsNoTracking().Where(x => x.State == SmsDispatchState.Pending && x.Purpose == SmsMessagePurpose.WalletMovement)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(20).ToListAsync(token);
        foreach (var id in ids) await delivery.DeliverAsync(db, id, token);
    }
}
