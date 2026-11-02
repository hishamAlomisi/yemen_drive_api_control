using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

/// <summary>Wallet history and its user notification share the same save and transaction.</summary>
public sealed class WalletMovementNotificationInterceptor : SaveChangesInterceptor
{
    private readonly HashSet<WalletTransaction> queued = new(ReferenceEqualityComparer.Instance);
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is YemenDriveDbContext db) {
            var movements = db.ChangeTracker.Entries<WalletTransaction>().Where(x => x.State == EntityState.Added && !queued.Contains(x.Entity)).Select(x => x.Entity).ToArray();
            if (movements.Length > 0) {
                var ids = movements.Select(x => x.WalletId).Distinct().ToArray();
                var owners = await db.Wallets.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.UserId, x.Currency, x.User.Role }).ToDictionaryAsync(x => x.Id, ct);
                foreach (var movement in movements) {
                    if (!owners.TryGetValue(movement.WalletId, out var owner)) continue;
                    var incoming = movement.Type is WalletTransactionType.Credit or WalletTransactionType.Refund or WalletTransactionType.Release;
                    db.Notifications.Add(new() { UserId = owner.UserId, Type = NotificationType.Payment,
                        Title = incoming ? "إيداع في المحفظة" : "سحب من المحفظة",
                        Body = $"{(incoming ? "أضيف إلى محفظتك" : "خُصم من محفظتك")} {movement.Amount.ToString("0.00", CultureInfo.InvariantCulture)} {owner.Currency}. الرصيد المتاح بعد العملية: {Math.Max(0, movement.BalanceAfter).ToString("0.00", CultureInfo.InvariantCulture)} {owner.Currency}." + (movement.BalanceAfter < 0 ? $" المديونية المتبقية: {-movement.BalanceAfter:0.00} {owner.Currency}." : ""),
                        DataJson = JsonSerializer.Serialize(new { kind = "wallet", action = owner.Role == UserRole.Driver ? "open_driver_wallet" : "open_wallet", reference = movement.ExternalReference }) });
                    queued.Add(movement);
                }
            }
        }
        return result;
    }
}
