using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;
public sealed record RidePickupModel(int RideId, string? Outcome = null);
public sealed class RidePickupService(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext user) : ModelService<RidePickupModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "contact"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, RidePickupModel m, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await RidePaymentGuard.LockAsync(db, m.RideId, ct);
        var ride = await db.Rides.SingleAsync(x => x.Id == m.RideId, ct);
        if (actor != ride.CustomerId && actor != ride.DriverId) throw new ServiceException("ride_access_denied", "هذه الرحلة لا تخصك.");
        if (operation == "contact")
        {
            if (actor != ride.DriverId || ride.Status != RideStatus.DriverArrived || string.IsNullOrWhiteSpace(m.Outcome) || m.Outcome.Length > 1000)
                throw new ServiceException("pickup_contact_not_available", "سجل نتيجة محاولة تواصل فعلية بعد الوصول وقبل الصعود.");
            db.PickupContactAttempts.Add(new() { RideId = ride.Id, ActorUserId = actor, Outcome = m.Outcome.Trim() }); await db.SaveChangesAsync(ct);
        }
        var contacts = await db.PickupContactAttempts.Where(x => x.RideId == ride.Id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Outcome, x.CreatedAtUtc }).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return new { ride.Id, ride.PickupGraceMinutes, ride.ArrivalToleranceMeters, contacts,
            noShowEligible = ride.Status == RideStatus.DriverArrived && ride.PassengerBoardedAtUtc == null && ride.DriverArrivedAtUtc != null &&
                ride.PickupGraceMinutes != null && DateTime.UtcNow >= ride.DriverArrivedAtUtc.Value.AddMinutes(ride.PickupGraceMinutes.Value) && contacts.Count > 0,
            notice = "عدم الحضور يحتاج وصولاً صحيحاً ومحاولة تواصل وسياسة مهلة معتمدة. لا تُفرض رسوم غير معتمدة؛ يمكن طلب الإلغاء ومراجعة المسؤولية." };
    }
    public static async Task VerifyArrivalAsync(YemenDriveDbContext db, YemenDrive.Database.Entities.Ride ride, CancellationToken ct)
    {
        var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, ct);
        if (location == null || !location.IsOnline || location.ObservedAtUtc < DateTime.UtcNow.AddSeconds(-30) || location.ObservedAtUtc > DateTime.UtcNow.AddSeconds(5))
            throw new ServiceException("arrival_location_unverified", "تعذر إثبات الوصول بموقع حديث. يمكن للراكب تأكيد وجودك وصعوده عند نقطة الانطلاق عبر مسار تأكيد الوصول دون GPS.");
        if (ride.ArrivalToleranceMeters == null)
            throw new ServiceException("arrival_policy_not_approved", "لم تعتمد الإدارة مسافة إثبات الوصول بعد. يمكن للراكب تأكيد وجود السائق وصعوده فعلياً من التطبيق.");
        if (RideWaitingCoordinator.DistanceMeters(location.Latitude, location.Longitude, ride.PickupLatitude, ride.PickupLongitude) > ride.ArrivalToleranceMeters)
            throw new ServiceException("driver_not_at_pickup", "موقع السائق خارج نطاق الانطلاق المعتمد. لا يكفي إرسال تنبيه لإثبات الوصول.");
    }
}
