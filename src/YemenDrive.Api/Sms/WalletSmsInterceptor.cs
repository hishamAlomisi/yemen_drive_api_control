using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api.Sms;

// Outbox rows commit in the same transaction as wallet movements; no network I/O here.
public sealed class WalletSmsInterceptor(SmsProviderConfigurationStore configuration, SmsPayloadProtector protector) : SaveChangesInterceptor
{
    private readonly HashSet<WalletTransaction> queued = new(ReferenceEqualityComparer.Instance);
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    { CaptureAsync(eventData.Context, default).GetAwaiter().GetResult(); return result; }
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { await CaptureAsync(eventData.Context, cancellationToken); return result; }

    private async Task CaptureAsync(DbContext? context, CancellationToken token)
    {
        if (context is not YemenDriveDbContext db) return;
        var movements = db.ChangeTracker.Entries<WalletTransaction>().Where(x => x.State == EntityState.Added && !queued.Contains(x.Entity)).Select(x => x.Entity).ToArray();
        if (movements.Length == 0 || !(await configuration.GetAsync(token)).HasPassword) return;
        var ids = movements.Select(x => x.WalletId).Distinct().ToArray();
        var owners = await db.Wallets.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Currency, x.User.PhoneNumber }).ToDictionaryAsync(x => x.Id, token);
        foreach (var movement in movements)
        {
            if (!owners.TryGetValue(movement.WalletId, out var owner)) continue;
            var number = SmsPayloadProtector.NormalizeNumber(owner.PhoneNumber);
            var verb = movement.Type switch {
                WalletTransactionType.Credit => "تم إيداع", WalletTransactionType.Debit => "تم خصم", WalletTransactionType.Refund => "تم رد",
                WalletTransactionType.Hold => "تم حجز", WalletTransactionType.Release => "تم تحرير حجز", WalletTransactionType.Commission => "تم خصم عمولة", _ => "تم تسجيل حركة بقيمة" };
            var text = $"يمن درايف: {verb} {movement.Amount.ToString("0.00", CultureInfo.InvariantCulture)} {owner.Currency} في محفظتك. الرصيد بعد الحركة: {Math.Max(0, movement.BalanceAfter).ToString("0.00", CultureInfo.InvariantCulture)} {owner.Currency}.";
            if (movement.BalanceAfter < 0) text += $" المديونية: {(-movement.BalanceAfter).ToString("0.00", CultureInfo.InvariantCulture)} {owner.Currency}.";
            db.SmsDispatches.Add(new() { Purpose = SmsMessagePurpose.WalletMovement, CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1), WalletTransaction = movement,
                ProtectedPayload = number is null ? null : protector.Protect(number, text),
                State = number is null ? SmsDispatchState.Rejected : SmsDispatchState.Pending, ErrorCode = number is null ? "invalid_sms_number" : null });
            queued.Add(movement);
        }
    }
}
