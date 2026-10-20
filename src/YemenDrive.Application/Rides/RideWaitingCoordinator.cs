using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Pricing;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public sealed class RideWaitingCoordinator(YemenDriveDbContext db, IRideRealtimePublisher? publisher = null)
{
    public const int ConnectionGraceSeconds = 120;

    public static decimal FeeAt(RideWaitingSession session, DateTime now)
    {
        if (session.StartedAtUtc is not DateTime started) return 0;
        var seconds = Math.Max(0, ((session.EndedAtUtc ?? now) - started).TotalSeconds - session.FreeMinutes * 60);
        return Math.Min(session.MaxFee, RidePricingPolicy.Round((decimal)seconds * session.PricePerMinute / 60));
    }

    // Caller holds the ride lock in a serializable transaction.
    public async Task<RideWaitingSession?> ReconcileAsync(RideEntity ride, DateTime now, CancellationToken ct)
    {
        var session = await db.RideWaitingSessions.SingleOrDefaultAsync(x => x.RideId == ride.Id && x.IsOpen, ct);
        if (session is null) return null;
        if (ride.Status != RideStatus.InProgress)
        {
            Close(ride, session, now, "ride_closed");
            return session;
        }
        if (session.StartedAtUtc is not DateTime started)
        {
            if (now >= session.CreatedAtUtc.AddMinutes(2)) Close(ride, session, now, "request_expired");
            return session;
        }
        var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, ct);
        var disconnectedAt = (location?.ObservedAtUtc ?? started).AddSeconds(ConnectionGraceSeconds);
        var cappedAt = started.AddMinutes(session.FreeMinutes).AddSeconds((double)(session.MaxFee * 60 / session.PricePerMinute));
        var endAt = disconnectedAt < cappedAt ? disconnectedAt : cappedAt;
        if (now >= endAt)
        {
            Close(ride, session, endAt < started ? started : endAt, disconnectedAt <= cappedAt ? "connection_lost" : "fee_cap");
            return session;
        }
        if (location is not null && location.ObservedAtUtc > session.LastMotionObservedAtUtc.GetValueOrDefault() &&
            location.ObservedAtUtc >= now.AddSeconds(-30))
        {
            var moved = location.Speed >= 2 && DistanceMeters(session.Latitude, session.Longitude, location.Latitude, location.Longitude) >= 80;
            if (moved)
            {
                session.MotionStartedAtUtc ??= now;
                if (now - session.MotionStartedAtUtc.Value >= TimeSpan.FromSeconds(10))
                {
                    Close(ride, session, session.MotionStartedAtUtc.Value, "vehicle_moving");
                    return session;
                }
            }
            else session.MotionStartedAtUtc = null;
            session.LastMotionObservedAtUtc = location.ObservedAtUtc;
        }
        var freeEnds = started.AddMinutes(session.FreeMinutes);
        if (!session.GraceWarningSent && session.FreeMinutes > 0 && now >= freeEnds.AddSeconds(-30) && now < freeEnds)
        {
            session.GraceWarningSent = true;
            Notify(ride, "ستنتهي مهلة الانتظار المجانية", "تبدأ رسوم الانتظار خلال 30 ثانية وفق السعر الذي وافقت عليه.");
        }
        if (!session.BillingNoticeSent && now >= freeEnds)
        {
            session.BillingNoticeSent = true;
            Notify(ride, "بدأ احتساب الانتظار", $"رسوم الدقيقة {session.PricePerMinute:0.##} ر.ي، وتُحسب بالثواني حتى الاستئناف أو بلوغ السقف.");
        }
        return session;
    }

    public void Close(RideEntity ride, RideWaitingSession session, DateTime at, string reason)
    {
        if (!session.IsOpen) return;
        session.EndedAtUtc = at;
        session.EndReason = reason;
        session.Fee = FeeAt(session, at);
        session.IsOpen = false;
        session.UpdatedAtUtc = at;
        if (session.StartedAtUtc is not null)
        {
            ride.WaitingFee += session.Fee;
            RidePricingPolicy.UpdateTotals(ride);
        }
        ride.UpdatedAtUtc = DateTime.UtcNow;
        Notify(ride, "انتهى الانتظار", $"رسوم هذا التوقف {session.Fee:0} ر.ي. إجمالي الانتظار للرحلة {ride.WaitingFee:0} ر.ي.");
    }

    public async Task EnsureSettledAsync(RideEntity ride, CancellationToken ct)
    {
        await RideRouteChangePolicy.EnsureNoOpenAsync(db, ride.Id, ct);
        if (await db.RideWaitingSessions.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct))
            throw new ServiceException("waiting_must_end", "أوقف الانتظار بزر استئناف الرحلة قبل فتح الدفع أو إنهاء الرحلة.");
    }

    public void Notify(RideEntity ride, string title, string body)
    {
        foreach (var userId in new[] { ride.CustomerId, ride.DriverId ?? ride.CustomerId }.Distinct())
            db.Notifications.Add(new Notification
            {
                UserId = userId, Type = NotificationType.RideStatus, Title = title, Body = body,
                DataJson = $"{{\"action\":\"open_active_ride\",\"rideId\":{ride.Id}}}"
            });
    }

    public Task PublishAsync(RideEntity ride, CancellationToken ct) =>
        publisher?.PublishRideStatusUpdatedAsync(RideRealtimeEventFactory.Status(ride), ct) ?? Task.CompletedTask;

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * r / 2), 2) + Math.Cos(lat1 * r) * Math.Cos(lat2 * r) * Math.Pow(Math.Sin((lon2 - lon1) * r / 2), 2);
        return 6371000 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
    }
}
