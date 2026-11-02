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
        var hasAnotherRide = await db.Rides.AnyAsync(x => x.Id != ride.Id && x.DriverId == driverId && x.ServiceEndedAtUtc == null &&
            (x.Status == RideStatus.DriverAssigned || x.Status == RideStatus.DriverEnRoute ||
             x.Status == RideStatus.DriverArrived || x.Status == RideStatus.InProgress || x.Status == RideStatus.CancellationPending), token);
        if (hasAnotherRide) return;
        var profile = await db.DriverProfiles.SingleOrDefaultAsync(x => x.UserId == driverId, token);
        if (profile == null) return;
        profile.UpdatedAtUtc = DateTime.UtcNow;
    }

    internal static async Task RequireAvailableAsync(YemenDriveDbContext db, int driverId, int rideId, CancellationToken token)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        await db.DriverProfiles.FromSqlInterpolated($"SELECT * FROM [DriverProfiles] WITH (UPDLOCK,HOLDLOCK) WHERE [UserId] = {driverId}").LoadAsync(token);
        var ride = await db.Rides.SingleAsync(x => x.Id == rideId, token);
        var profile = await db.DriverProfiles.SingleOrDefaultAsync(x => x.UserId == driverId, token);
        if (profile == null || profile.ServiceKindId != ride.ServiceKindId || profile.ServiceCatalogItemId != ride.ServiceCatalogItemId ||
            profile.ApprovedPassengerSeats == null || profile.ApprovedPassengerSeats < (ride.PassengerCount ?? (ride.MoreThanFourPassengers ? 5 : 1)))
            throw new YemenDrive.Shared.Api.ServiceException("vehicle_capacity_not_approved", "المركبة لا تستوفي عدد الركاب أو لم تعتمد الإدارة سعتها الفعلية بعد.");
        if (!await db.DriverProfiles.AnyAsync(x => x.UserId == driverId && x.IsAvailable && x.AcceptsNewRequests && x.IsApproved && x.User.IsActive, token) ||
            !await db.DriverLiveLocations.AnyAsync(x => x.DriverId == driverId && x.IsOnline && x.ObservedAtUtc >= cutoff, token) ||
            await db.Rides.AnyAsync(x => x.Id != rideId && x.DriverId == driverId && x.ServiceEndedAtUtc == null &&
                (x.Status == RideStatus.DriverAssigned || x.Status == RideStatus.DriverEnRoute || x.Status == RideStatus.DriverArrived ||
                 x.Status == RideStatus.InProgress || x.Status == RideStatus.CancellationPending), token))
            throw new YemenDrive.Shared.Api.ServiceException("driver_not_available", "السائق لم يعد متاحاً لهذه الرحلة. اختر عرضاً آخر.");
    }
}
