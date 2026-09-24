using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Drivers;

public sealed class NearbyDriverReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : ReportsService<NearbyDriverModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(
        NearbyDriverModel model,
        CancellationToken cancellationToken)
    {
        _ = currentUser.RequireUserId();
        if (model.PickupLatitude is < -90 or > 90 ||
            model.PickupLongitude is < -180 or > 180)
            throw new ServiceException("invalid_pickup_location", "إحداثيات نقطة الانطلاق غير صحيحة.");

        var radiusMeters = Math.Clamp(model.RadiusMeters, 250, 25000);
        var locationFreshAfter = DateTime.UtcNow.AddMinutes(-5);
        var query = dbContext.DriverProfiles.AsNoTracking()
            .Where(profile => profile.User.IsActive && profile.IsAvailable)
            .Join(
                dbContext.DriverLiveLocations.AsNoTracking().Where(location => location.IsOnline && location.ObservedAtUtc >= locationFreshAfter),
                profile => profile.UserId,
                location => location.DriverId,
                (profile, location) => new { profile, location })
            .Where(item => !dbContext.Rides.Any(ride =>
                ride.DriverId == item.profile.UserId &&
                (ride.Status == RideStatus.DriverAssigned ||
                 ride.Status == RideStatus.DriverEnRoute ||
                 ride.Status == RideStatus.InProgress)));

        if (model.ServiceKindId is int serviceKindId)
            query = query.Where(item => item.profile.ServiceKindId == serviceKindId);
        if (model.ServiceCatalogItemId is int serviceCatalogItemId)
            query = query.Where(item => item.profile.ServiceCatalogItemId == serviceCatalogItemId);

        var candidates = await query
            .OrderByDescending(item => item.profile.Rating)
            .Take(200)
            .Select(item => new
            {
                DriverId = item.profile.UserId,
                Name = item.profile.User.DisplayName,
                item.profile.PhotoUrl,
                item.profile.VehicleModel,
                item.profile.PlateNumber,
                item.profile.Rating,
                item.profile.ServiceCatalogItem.Code,
                Latitude = item.location.Latitude,
                Longitude = item.location.Longitude,
                item.location.Bearing,
                item.location.ObservedAtUtc,
                CompletedTrips = dbContext.Rides.Count(ride =>
                    ride.DriverId == item.profile.UserId && ride.Status == RideStatus.Completed)
            })
            .ToListAsync(cancellationToken);

        return candidates
            .Select(item => new
            {
                item.DriverId,
                name = item.Name ?? "سائق يمن درايف",
                item.PhotoUrl,
                item.VehicleModel,
                item.PlateNumber,
                rating = item.Rating,
                serviceCode = item.Code,
                item.Latitude,
                item.Longitude,
                item.Bearing,
                item.ObservedAtUtc,
                item.CompletedTrips,
                distanceMeters = HaversineMeters(
                    model.PickupLatitude,
                    model.PickupLongitude,
                    item.Latitude,
                    item.Longitude)
            })
            .Where(item => item.distanceMeters <= radiusMeters)
            .OrderBy(item => item.distanceMeters)
            .Take(50)
            .ToArray();
    }

    private static double HaversineMeters(
        double firstLatitude,
        double firstLongitude,
        double secondLatitude,
        double secondLongitude)
    {
        const double earthRadiusMeters = 6371000d;
        var latitudeDelta = DegreesToRadians(secondLatitude - firstLatitude);
        var longitudeDelta = DegreesToRadians(secondLongitude - firstLongitude);
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
                Math.Cos(DegreesToRadians(firstLatitude)) * Math.Cos(DegreesToRadians(secondLatitude)) *
                Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return 2 * earthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
