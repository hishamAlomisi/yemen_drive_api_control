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

public interface IRideParticipantPresence { bool IsForeground(int userId); }
public sealed record RideArrivalModel(int RideId, Guid? AlertId = null);
public sealed record RideArrivalView(int RideId, bool Arrived, bool RiderOnline, string DriverName,
    bool Acknowledged, Guid? AlertId, bool AlertActive, bool CanRing);

public sealed class RideArrival(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext user,
    IRideRealtimePublisher publisher, IRideParticipantPresence presence) : ModelService<RideArrivalModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "acknowledge", "ring", "stop"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, RideArrivalModel model, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await RidePaymentGuard.LockAsync(db, model.RideId, token);
        var ride = await db.Rides.Include(x => x.Driver).SingleOrDefaultAsync(x => x.Id == model.RideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (user.UserId != ride.CustomerId && user.UserId != ride.DriverId)
            throw new ServiceException("ride_access_denied", "هذا التنبيه خاص بأطراف الرحلة.");
        var arrived = ride.Status == RideStatus.DriverArrived && ride.PassengerBoardedAtUtc == null;
        var changed = false;
        if (operation != "get" && !arrived) throw new ServiceException("arrival_not_active", "التنبيه متاح بعد وصول السائق وقبل بدء الرحلة فقط.");
        if (operation is "acknowledge" or "stop")
        {
            if (user.UserId != ride.CustomerId) throw new ServiceException("customer_required", "تأكيد استلام التنبيه متاح للراكب فقط.");
            if (operation == "stop")
            {
                if (model.AlertId is null || model.AlertId != ride.ArrivalAlertId)
                    throw new ServiceException("arrival_alert_changed", "هذا التنبيه قديم؛ حدّث حالة الوصول.");
                if (ride.ArrivalAlertStoppedAtUtc is null) { ride.ArrivalAlertStoppedAtUtc = DateTime.UtcNow; changed = true; }
            }
            if (ride.DriverArrivalAcknowledgedAtUtc is null) { ride.DriverArrivalAcknowledgedAtUtc = DateTime.UtcNow; changed = true; }
            if (changed) db.Notifications.Add(new Notification { UserId = ride.DriverId!.Value, Type = NotificationType.RideStatus,
                Title = "أكد الراكب استلام تنبيه الوصول", Body = operation == "stop" ? "شاهد الراكب تنبيهك وأوقف الرنين." : "شاهد الراكب إشعار وصولك وأكد استلامه.",
                DataJson = $"{{\"action\":\"open_active_ride\",\"rideId\":{ride.Id}}}" });
        }
        else if (operation == "ring")
        {
            if (user.UserId != ride.DriverId) throw new ServiceException("driver_required", "إرسال التنبيه متاح للسائق المسند فقط.");
            if (ride.ArrivalAlertId is null || ride.ArrivalAlertStoppedAtUtc is not null) ride.ArrivalAlertId = Guid.NewGuid();
            ride.ArrivalAlertSentAtUtc = DateTime.UtcNow;
            ride.ArrivalAlertStoppedAtUtc = null;
            changed = true;
            db.Notifications.Add(new Notification { UserId = ride.CustomerId, Type = NotificationType.RideStatus,
                Title = "السائق وصل وينتظرك", Body = $"{ride.Driver?.DisplayName ?? "السائق"} ينتظرك عند نقطة الانطلاق. افتح تنبيه الوصول.",
                DataJson = $"{{\"action\":\"ride_arrival_alert\",\"rideId\":{ride.Id},\"alertId\":\"{ride.ArrivalAlertId}\"}}" });
            if (!presence.IsForeground(ride.CustomerId)) db.Notifications.Add(new Notification { UserId = ride.DriverId!.Value,
                Type = NotificationType.RideStatus, Title = "الراكب غير متصل بالتطبيق حالياً", Body = "أُرسل إشعار وصول. يمكنك التواصل معه مباشرة عبر مكالمة هاتفية.",
                DataJson = $"{{\"action\":\"open_active_ride\",\"rideId\":{ride.Id}}}" });
        }
        if (changed) await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        var view = View(ride, presence.IsForeground(ride.CustomerId));
        if (changed) await publisher.PublishArrivalUpdatedAsync(new[] { ride.CustomerId, ride.DriverId!.Value }, view, token);
        return view;
    }
    public static RideArrivalView View(YemenDrive.Database.Entities.Ride ride, bool online) => new(ride.Id,
        ride.Status == RideStatus.DriverArrived && ride.PassengerBoardedAtUtc == null, online, ride.Driver?.DisplayName ?? "السائق",
        ride.DriverArrivalAcknowledgedAtUtc is not null, ride.ArrivalAlertId,
        ride.Status == RideStatus.DriverArrived && ride.PassengerBoardedAtUtc == null && ride.ArrivalAlertSentAtUtc is not null && ride.ArrivalAlertStoppedAtUtc is null,
        ride.Status == RideStatus.DriverArrived && ride.PassengerBoardedAtUtc == null);
}
