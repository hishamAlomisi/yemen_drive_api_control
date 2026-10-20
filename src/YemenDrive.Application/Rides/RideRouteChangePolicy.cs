using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public static class RideRouteChangePolicy
{
    public static async Task ExpireAsync(YemenDriveDbContext db, RideEntity ride, DateTime now, CancellationToken ct)
    {
        var request = await db.RideRouteChanges.SingleOrDefaultAsync(x => x.RideId == ride.Id && x.IsOpen, ct);
        if (request == null) return;
        var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, ct);
        if (request.ExpiresAtUtc > now && ride.Status == RideStatus.InProgress && !ride.CustomerPaymentEnabled &&
            request.RouteVersion == ride.RouteVersion && request.PreviousTotal == ride.TotalAmount &&
            (location == null || RideWaitingCoordinator.DistanceMeters(request.ChangeLatitude, request.ChangeLongitude, location.Latitude, location.Longitude) <= 80)) return;
        var notified = request.Status == "pending";
        request.IsOpen = false; request.Status = "expired"; request.DecidedAtUtc = now; request.UpdatedAtUtc = now;
        if (notified) new RideWaitingCoordinator(db).Notify(ride, "انتهى عرض تغيير المسار", "بقي المسار الحالي؛ يلزم عرض جديد لاعتماد وجهة أخرى.");
    }
    public static async Task EnsureNoOpenAsync(YemenDriveDbContext db, int rideId, CancellationToken ct)
    {
        if (await db.RideRouteChanges.AnyAsync(x => x.RideId == rideId && x.IsOpen && x.ExpiresAtUtc > DateTime.UtcNow, ct))
            throw new ServiceException("route_change_pending", "أكمل أو ألغِ طلب تغيير المسار قبل الانتظار أو فتح الدفع.");
    }

    public static async Task<DriverLiveLocation> RequireLocationAsync(YemenDriveDbContext db, RideEntity ride, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, ct);
        if (location == null || !location.IsOnline || location.ObservedAtUtc < now.AddSeconds(-30) || location.ObservedAtUtc > now.AddSeconds(5))
            throw new ServiceException("route_location_required", "يلزم موقع حديث ومتصل للسائق لحساب المسار.");
        return location;
    }

    public static async Task StartTrackingAsync(YemenDriveDbContext db, RideEntity ride, DateTime now, CancellationToken ct)
    {
        if (!ride.HasRoutePricingSnapshot) return;
        var location = await RequireLocationAsync(db, ride, ct);
        ride.RouteSegmentStartedAtUtc = now;
        ride.RouteSegmentLatitude = location.Latitude;
        ride.RouteSegmentLongitude = location.Longitude;
        db.LocationUpdates.Add(new LocationUpdate { RideId = ride.Id, ActorId = ride.DriverId!.Value,
            Latitude = location.Latitude, Longitude = location.Longitude, Speed = location.Speed, ObservedAtUtc = now });
    }

    public static async Task<(double Meters, double Seconds)> MeasureAsync(YemenDriveDbContext db, RideEntity ride,
        DriverLiveLocation location, CancellationToken ct)
    {
        if (ride.RouteSegmentStartedAtUtc is not { } start || location.ObservedAtUtc < start)
            throw new ServiceException("route_track_unavailable", "لا يوجد سجل موثوق للجزء المقطوع من هذه الرحلة.");
        var points = await db.LocationUpdates.AsNoTracking()
            .Where(x => x.RideId == ride.Id && x.ActorId == ride.DriverId && x.ObservedAtUtc >= start && x.ObservedAtUtc <= location.ObservedAtUtc)
            .OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
        var latitude = ride.RouteSegmentLatitude;
        var longitude = ride.RouteSegmentLongitude;
        var previousAt = start;
        var previousLatitude = latitude;
        var previousLongitude = longitude;
        double distance = 0;
        foreach (var point in points.Append(new LocationUpdate { Latitude = location.Latitude, Longitude = location.Longitude,
                     ObservedAtUtc = location.ObservedAtUtc, Speed = location.Speed }))
        {
            if (point.ObservedAtUtc < previousAt) continue;
            var gap = (point.ObservedAtUtc - previousAt).TotalSeconds;
            var jump = RideWaitingCoordinator.DistanceMeters(previousLatitude, previousLongitude, point.Latitude, point.Longitude);
            if (gap > 120 || jump > 75 * gap + 75)
                throw new ServiceException("route_track_incomplete", "سجل المسافة يحتوي انقطاعًا أو موقعًا غير موثوق؛ تعذر تسعير تغيير المسار بدقة.");
            var moved = RideWaitingCoordinator.DistanceMeters(latitude, longitude, point.Latitude, point.Longitude);
            if (point.Speed is null or >= 0.8 && moved >= Math.Max(5, Math.Min(point.Accuracy ?? 5, 10)))
            {
                distance += moved;
                latitude = point.Latitude; longitude = point.Longitude;
            }
            else if (point.Speed is < 0.8)
            {
                latitude = point.Latitude; longitude = point.Longitude;
            }
            previousAt = point.ObservedAtUtc;
            previousLatitude = point.Latitude; previousLongitude = point.Longitude;
        }
        // Waiting already has its own charge. Remove its entire elapsed time
        // (including the free allowance) from the metered driving-time portion.
        var stops = await db.RideWaitingSessions.AsNoTracking().Where(x => x.RideId == ride.Id &&
            x.StartedAtUtc != null && x.EndedAtUtc != null && x.EndedAtUtc > start && x.StartedAtUtc < location.ObservedAtUtc).ToListAsync(ct);
        var intervals = stops.Select(x => (Start: x.StartedAtUtc!.Value > start ? x.StartedAtUtc.Value : start,
                End: x.EndedAtUtc!.Value < location.ObservedAtUtc ? x.EndedAtUtc.Value : location.ObservedAtUtc)).OrderBy(x => x.Start);
        double stopped = 0;
        var lastEnd = start;
        foreach (var interval in intervals)
        {
            var begins = interval.Start > lastEnd ? interval.Start : lastEnd;
            if (interval.End > begins) stopped += (interval.End - begins).TotalSeconds;
            if (interval.End > lastEnd) lastEnd = interval.End;
        }
        return (distance, Math.Max(0, (location.ObservedAtUtc - start).TotalSeconds - stopped));
    }
}
