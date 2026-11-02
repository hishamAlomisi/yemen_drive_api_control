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

public sealed record RideServiceModel(int RideId, string? Reason = null);
public sealed class RideServiceControl(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user, IRideRealtimePublisher? realtime = null) : ModelService<RideServiceModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "board", "confirmPickup", "finish"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, RideServiceModel model, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await RidePaymentGuard.LockAsync(db, model.RideId, ct);
        var ride = await db.Rides.SingleAsync(x => x.Id == model.RideId, ct);
        var id = user.RequireUserId();
        if (id != ride.CustomerId && id != ride.DriverId) throw new ServiceException("ride_access_denied", "هذه الرحلة لا تخصك.");
        if (operation == "confirmPickup")
        {
            if (id != ride.CustomerId || ride.Status is not (RideStatus.DriverAssigned or RideStatus.DriverEnRoute) || ride.ServiceEndedAtUtc != null || string.IsNullOrWhiteSpace(model.Reason))
                throw new ServiceException("pickup_confirmation_not_available", "أكد وجود السائق وصعودك فعلياً بعد اللقاء. هذا مسار إثبات بديل عند تعذر GPS.");
            ride.RiderVerifiedPickupAtUtc = DateTime.UtcNow;
            ride.DriverArrivedAtUtc ??= DateTime.UtcNow;
            ride.Status = RideStatus.DriverArrived;
            db.OperationalDecisionAudits.Add(new() { ActorUserId = id, Subject = "RidePickupException", SubjectId = ride.Id, Action = "rider_verified", Reason = model.Reason.Trim() });
        }
        if (operation is "board" or "confirmPickup")
        {
            if (id != ride.CustomerId || ride.Status != RideStatus.DriverArrived || ride.ServiceEndedAtUtc != null)
                throw new ServiceException("boarding_not_available", "أكد صعودك إلى السيارة بعد وصول السائق وقبل بدء الرحلة.");
            ride.PassengerBoardedAtUtc ??= DateTime.UtcNow;
            db.Notifications.Add(new Notification { UserId = ride.DriverId!.Value, Type = NotificationType.RideStatus,
                Title = "أكد الراكب صعوده", Body = "أكد الراكب أنه صعد إلى السيارة. يمكنك بدء الرحلة بعد استيفاء الدفع المختار.",
                DataJson = $"{{\"action\":\"open_active_ride\",\"rideId\":{ride.Id}}}" });
        }
        if (operation == "finish" && ride.ServiceEndedAtUtc == null)
        {
            var cancellation = await db.RideCancellationRequests.AnyAsync(x => x.RideId == ride.Id, ct);
            if (id != ride.DriverId && !cancellation) throw new ServiceException("driver_required", "إنهاء النقل المعتاد متاح للسائق المسند.");
            if (ride.Status is not (RideStatus.InProgress or RideStatus.CancellationPending))
                throw new ServiceException("service_not_active", "هذه الخدمة ليست جارية.");
            if (string.IsNullOrWhiteSpace(model.Reason)) throw new ServiceException("reason_required", "أكد أن النقل انتهى فعلياً عند نقطة آمنة، واذكر سبب الإنهاء.");
            await RideFinancialState.CloseServiceAsync(db, ride, model.Reason.Trim(), ct);
            if (!cancellation) { ride.Status = RideStatus.Completed; ride.CompletedAtUtc ??= DateTime.UtcNow; }
            db.OperationalDecisionAudits.Add(new() { ActorUserId = id, Subject = "RideService", SubjectId = ride.Id, Action = "finish", Reason = model.Reason.Trim() });
        }
        await db.SaveChangesAsync(ct);
        if (ride.ServiceEndedAtUtc != null && ride.Status == RideStatus.Completed)
            await RideFinancialState.SynchronizeAsync(db, ride, id, ct);
        var money = await RideFinancialState.ReadAsync(db, ride, ct);
        await tx.CommitAsync(ct);
        if (operation != "get" && realtime != null) await realtime.PublishRideStatusUpdatedAsync(RideRealtimeEventFactory.Status(ride), ct);
        return new { ride.Id, ride.PassengerBoardedAtUtc, ride.ServiceEndedAtUtc,
            operationalStatus = ride.ServiceEndedAtUtc != null && ride.Status == RideStatus.CancellationPending ? RideStatus.Cancelled : ride.Status,
            finance = money };
    }
}
