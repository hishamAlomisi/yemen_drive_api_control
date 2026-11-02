using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;
public static class OperationalReviewCoordinator
{
    public static Task<int?> OwnerAsync(YemenDriveDbContext db, string permission, CancellationToken ct) =>
        db.Users.Where(x => x.IsActive && x.Role == UserRole.Admin && (x.IsSystemAdministrator || db.AdministratorPermissions.Any(p => p.UserId == x.Id && (p.Permission == "*" || p.Permission == permission))))
        .OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
    private static async Task<bool> EligibleOwnerAsync(YemenDriveDbContext db, int? owner, CancellationToken ct) =>
        owner != null && await db.Users.AnyAsync(x => x.Id == owner && x.IsActive && x.Role == UserRole.Admin && (x.IsSystemAdministrator || db.AdministratorPermissions.Any(p => p.UserId == x.Id && (p.Permission == "*" || p.Permission == "refunds.write"))), ct);
    public static async Task ReconcileAsync(YemenDriveDbContext db, CancellationToken ct)
    {
        var ids = await db.RideCancellationRequests.AsNoTracking().Where(x => x.Status == RideCancellationStatus.DriverReviewPending || x.Status == RideCancellationStatus.CustomerReviewPending ||
            x.Status == RideCancellationStatus.RefundMethodPending || x.Status == RideCancellationStatus.RefundConsentPending || x.Status == RideCancellationStatus.CashReceiptPending ||
            x.Status == RideCancellationStatus.AdminReviewPending || x.Status == RideCancellationStatus.FinancialReviewPending).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var rideId = await db.RideCancellationRequests.AsNoTracking().Where(x => x.Id == id).Select(x => x.RideId).SingleAsync(ct);
            await RidePaymentGuard.LockAsync(db, rideId, ct);
            var item = await db.RideCancellationRequests.Include(x => x.Ride).SingleAsync(x => x.Id == id, ct);
            await db.Entry(item).ReloadAsync(ct);
            await db.Entry(item.Ride).ReloadAsync(ct);
            if (item.Status is not (RideCancellationStatus.DriverReviewPending or RideCancellationStatus.CustomerReviewPending or RideCancellationStatus.RefundMethodPending or RideCancellationStatus.RefundConsentPending or RideCancellationStatus.CashReceiptPending or RideCancellationStatus.AdminReviewPending or RideCancellationStatus.FinancialReviewPending))
            { await tx.CommitAsync(ct); db.ChangeTracker.Clear(); continue; }
            if (!await EligibleOwnerAsync(db, item.AssignedAdminId, ct)) item.AssignedAdminId = await OwnerAsync(db, "refunds.write", ct);
            if (item.Ride.DecisionResponseMinutes != null) item.ReviewDueAtUtc ??= item.CreatedAtUtc.AddMinutes(item.Ride.DecisionResponseMinutes.Value);
            if (item.ReviewDueAtUtc <= DateTime.UtcNow && item.Status is RideCancellationStatus.DriverReviewPending or RideCancellationStatus.CustomerReviewPending or RideCancellationStatus.RefundMethodPending or RideCancellationStatus.RefundConsentPending or RideCancellationStatus.CashReceiptPending)
            {
                item.Status = RideCancellationStatus.FinancialReviewPending;
                db.OperationalDecisionAudits.Add(new() { Subject = "RideCancellation", SubjectId = item.Id, Action = "response_window_expired", Reason = "انتهت مهلة الرد المعتمدة للرحلة؛ أحيلت المعالجة لمسؤول، دون إثبات استلام النقد أو إسقاط أي حق." });
                foreach (var recipient in new[] { item.CustomerId, item.DriverId ?? 0, item.AssignedAdminId ?? 0 }.Where(x => x > 0).Distinct())
                    db.Notifications.Add(new() { UserId = recipient, Type = NotificationType.Payment, Title = "أُحيلت المعالجة المالية للإدارة", Body = "انتهت مهلة الرد المعتمدة. لم يُثبت استلام النقد أو الاسترداد من عدم الرد. راجع متابعة الرحلة.", DataJson = $"{{\"action\":\"open_ride_payment\",\"rideId\":{item.RideId}}}" });
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); db.ChangeTracker.Clear();
        }
        var cases = await db.RideFinancialCases.AsNoTracking().Where(x => x.Status != "Resolved").Select(x => new { x.Id, x.RideId }).ToListAsync(ct);
        foreach (var candidate in cases)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await RidePaymentGuard.LockAsync(db, candidate.RideId, ct);
            var item = await db.RideFinancialCases.SingleAsync(x => x.Id == candidate.Id, ct);
            await db.Entry(item).ReloadAsync(ct);
            if (item.Status != "Resolved" && !await EligibleOwnerAsync(db, item.AssignedAdminId, ct))
                item.AssignedAdminId = await OwnerAsync(db, "refunds.write", ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); db.ChangeTracker.Clear();
        }
    }
}
