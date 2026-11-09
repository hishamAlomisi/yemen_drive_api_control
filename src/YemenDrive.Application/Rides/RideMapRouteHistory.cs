using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public sealed record RideMapRouteSample(double Latitude, double Longitude, double? Accuracy, DateTime ObservedAtUtc);

internal static class RideMapRouteHistory
{
    // Display-only history. Fare calculations continue to use the full archive.
    // One actual observation per time bucket bounds the snapshot while keeping
    // its beginning after a destination change or a client reconnect.
    public static async Task<IReadOnlyList<RideMapRouteSample>> ReadAsync(
        YemenDriveDbContext db, RideEntity ride, CancellationToken token)
    {
        if (ride.DriverId is not int driverId || ride.StartedAtUtc is not DateTime start)
            return [];
        var end = ride.ServiceEndedAtUtc ?? DateTime.UtcNow;
        if (end < start) return [];
        var secondsPerBucket = Math.Max(1, (int)Math.Ceiling((end - start).TotalSeconds / 600));
        var query = db.LocationUpdates.AsNoTracking().Where(x =>
            x.RideId == ride.Id && x.ActorId == driverId &&
            x.ObservedAtUtc >= start && x.ObservedAtUtc <= end &&
            x.Latitude >= -90 && x.Latitude <= 90 && x.Longitude >= -180 && x.Longitude <= 180 &&
            (x.Accuracy == null || x.Accuracy > 0 && x.Accuracy <= 75));
        var samples = await query
            .GroupBy(x => EF.Functions.DateDiffSecond(start, x.ObservedAtUtc) / secondsPerBucket)
            .Select(group => group.OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.Id)
                .Select(x => new RideMapRouteSample(x.Latitude, x.Longitude, x.Accuracy, x.ObservedAtUtc)).First())
            .ToListAsync(token);
        var last = await query.OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new RideMapRouteSample(x.Latitude, x.Longitude, x.Accuracy, x.ObservedAtUtc))
            .FirstOrDefaultAsync(token);
        if (last is not null) samples.Add(last);
        return samples.Distinct().OrderBy(x => x.ObservedAtUtc).ToArray();
    }
}
