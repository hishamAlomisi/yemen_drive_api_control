using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

internal static class RideDriverAvailability
{
    // Ride.DriverId remains a historical/accounting relationship. Active ride
    // queries use status; no mutable active-trip foreign key exists on drivers.
    internal static async Task ReleaseAsync(YemenDriveDbContext db, YemenDrive.Database.Entities.Ride ride, CancellationToken token)
    {
        if (ride.DriverId is not int driverId) return;
        var hasAnotherRide = await db.Rides.AnyAsync(x => x.Id != ride.Id && x.DriverId == driverId &&
            (x.Status == RideStatus.DriverAssigned || x.Status == RideStatus.DriverEnRoute ||
             x.Status == RideStatus.DriverArrived || x.Status == RideStatus.InProgress || x.Status == RideStatus.CancellationPending), token);
        if (hasAnotherRide) return;
        var profile = await db.DriverProfiles.SingleOrDefaultAsync(x => x.UserId == driverId, token);
        if (profile == null) return;
        profile.IsAvailable = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
    }

    internal static async Task RequireAvailableAsync(YemenDriveDbContext db, int driverId, int rideId, CancellationToken token)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        if (!await db.DriverProfiles.AnyAsync(x => x.UserId == driverId && x.IsAvailable && x.User.IsActive, token) ||
            !await db.DriverLiveLocations.AnyAsync(x => x.DriverId == driverId && x.IsOnline && x.ObservedAtUtc >= cutoff, token) ||
            await db.Rides.AnyAsync(x => x.Id != rideId && x.DriverId == driverId &&
                (x.Status == RideStatus.DriverAssigned || x.Status == RideStatus.DriverEnRoute || x.Status == RideStatus.DriverArrived ||
                 x.Status == RideStatus.InProgress || x.Status == RideStatus.CancellationPending), token))
            throw new YemenDrive.Shared.Api.ServiceException("driver_not_available", "السائق لم يعد متاحاً لهذه الرحلة. اختر عرضاً آخر.");
    }
}
