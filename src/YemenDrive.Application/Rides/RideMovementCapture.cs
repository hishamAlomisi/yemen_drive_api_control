using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public static class RideMovementCapture
{
    public static async Task<RideMovementSnapshot> RequestAsync(YemenDriveDbContext db, RideEntity ride,
        RideModel? request, CancellationToken ct)
    {
        var snapshot = db.RideMovementSnapshots.Local.FirstOrDefault(x => x.RideId == ride.Id) ??
            await db.RideMovementSnapshots.SingleOrDefaultAsync(x => x.RideId == ride.Id, ct);
        if (snapshot is not null) return snapshot;
        snapshot = new RideMovementSnapshot
        {
            RideId = ride.Id, RequestedAtUtc = ride.CreatedAtUtc,
            PickupLatitude = ride.PickupLatitude, PickupLongitude = ride.PickupLongitude,
            PickupLabel = RideLocationText.DisplayName(ride.PickupLabel, ride.PickupAddress, "نقطة الانطلاق"),
            DestinationLatitude = ride.DestinationLatitude, DestinationLongitude = ride.DestinationLongitude,
            DestinationLabel = RideLocationText.DisplayName(ride.DestinationLabel, ride.DestinationAddress, "الوجهة"),
            PlannedRoutePolyline = ride.RoutePolyline,
            PlannedDurationSeconds = ride.RouteVersion == 0 && ride.RouteQuotedDurationSeconds > 0
                ? ride.RouteQuotedDurationSeconds : null
        };
        var now = DateTime.UtcNow;
        if (request?.PassengerLatitude is double lat && request.PassengerLongitude is double lon &&
            double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180 &&
            request.PassengerAccuracy is double accuracy && double.IsFinite(accuracy) && accuracy is > 0 and <= 75 &&
            request.PassengerObservedAtUtc is { } observed && observed.UtcDateTime >= now.AddMinutes(-2) && observed.UtcDateTime <= now.AddSeconds(5))
        {
            snapshot.PassengerLatitude = lat; snapshot.PassengerLongitude = lon;
            snapshot.PassengerAccuracy = accuracy; snapshot.PassengerObservedAtUtc = observed.UtcDateTime;
        }
        db.RideMovementSnapshots.Add(snapshot);
        return snapshot;
    }

    public static async Task AcceptedAsync(YemenDriveDbContext db, RideEntity ride, DateTime acceptedAtUtc, CancellationToken ct)
    {
        var snapshot = await RequestAsync(db, ride, null, ct);
        snapshot.AcceptedAtUtc ??= acceptedAtUtc;
    }
}
