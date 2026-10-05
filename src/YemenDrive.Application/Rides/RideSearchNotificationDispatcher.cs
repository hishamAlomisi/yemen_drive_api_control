using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

internal static class RideSearchNotificationDispatcher
{
    public static string RideData(int rideId) => $"{{\"rideId\":{rideId}}}";

    public static async Task<int> NotifyAvailableDriversAsync(
        YemenDriveDbContext db,
        RideEntity ride,
        CancellationToken token)
    {
        var radiusMeters = RideDriverProximity.RadiusMeters(ride, DateTime.UtcNow);
        var locationFreshAfter = DateTime.UtcNow.AddMinutes(-5);
        var driverIds = (await db.DriverProfiles.AsNoTracking()
            .Where(profile => profile.User.IsActive && profile.IsAvailable &&
                              profile.ServiceKindId == ride.ServiceKindId &&
                              profile.ServiceCatalogItemId == ride.ServiceCatalogItemId &&
                              !db.Rides.Any(activeRide =>
                                  activeRide.DriverId == profile.UserId &&
                                  (activeRide.Status == RideStatus.DriverAssigned ||
                                   activeRide.Status == RideStatus.DriverEnRoute ||
                                   activeRide.Status == RideStatus.InProgress)))
            .Join(db.DriverLiveLocations.AsNoTracking()
                    .Where(location => location.IsOnline &&
                                       location.ObservedAtUtc >= locationFreshAfter),
                profile => profile.UserId,
                location => location.DriverId,
                (profile, location) => new
                {
                    profile.UserId,
                    location.Latitude,
                    location.Longitude
                })
            .Take(200)
            .ToListAsync(token))
            .Where(candidate => RideDriverProximity.HaversineMeters(
                ride.PickupLatitude,
                ride.PickupLongitude,
                candidate.Latitude,
                candidate.Longitude) <= radiusMeters)
            .Select(candidate => candidate.UserId)
            .Distinct()
            .ToList();

        var routeSummary = RideLocationText.RouteSummary(
            ride.PickupLabel,
            ride.PickupAddress,
            ride.DestinationLabel,
            ride.DestinationAddress);
        foreach (var driverId in driverIds)
        {
            db.Notifications.Add(new Notification
            {
                UserId = driverId,
                Type = NotificationType.RideOffer,
                Title = "طلب رحلة جديد",
                Body = $"يوجد طلب جديد {routeSummary}.",
                DataJson = RideData(ride.Id)
            });
        }

        return driverIds.Count;
    }
}
