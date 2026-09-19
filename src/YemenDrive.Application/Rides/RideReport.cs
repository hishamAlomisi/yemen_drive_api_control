using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed class RideReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : ReportsService<RideModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(RideModel model, CancellationToken cancellationToken)
    {
        var query = dbContext.Rides.AsNoTracking().AsQueryable();
        var currentUserId = currentUser.UserId;
        var isAdmin = currentUserId is int adminId && await dbContext.Users.AsNoTracking()
            .AnyAsync(x => x.Id == adminId && x.Role == UserRole.Admin && x.IsActive, cancellationToken);
        DriverProfile? driverProfile = null;
        if (!isAdmin && currentUserId is int userId)
        {
            driverProfile = await dbContext.DriverProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);

            if (driverProfile is null)
            {
                query = query.Where(x => x.CustomerId == userId || x.DriverId == userId);
            }
            else
            {
                // A driver can see rides matching their assigned service while
                // they are still open for offers, in addition to their rides.
                // An assigned/en-route/in-progress ride makes the driver busy;
                // it must not be possible to accept a second customer at once.
                query = query.Where(x =>
                    x.DriverId == userId ||
                    (x.DriverId == null &&
                     (x.Status == RideStatus.Searching || x.Status == RideStatus.Negotiating) &&
                     x.ServiceKindId == driverProfile.ServiceKindId &&
                     x.ServiceCatalogItemId == driverProfile.ServiceCatalogItemId &&
                     !dbContext.Rides.Any(activeRide =>
                         activeRide.DriverId == userId &&
                         (activeRide.Status == RideStatus.DriverAssigned ||
                          activeRide.Status == RideStatus.DriverEnRoute ||
                          activeRide.Status == RideStatus.InProgress))));
            }
        }
        if (!isAdmin && currentUserId is null && model.CustomerId is not null)
            query = query.Where(x => x.CustomerId == model.CustomerId);
        if (model.Status is not null) query = query.Where(x => x.Status == model.Status);
        var rides = await query
            .Include(x => x.Customer)
            .Include(x => x.Driver)
            .Include(x => x.ServiceKind)
            .Include(x => x.ServiceCatalogItem)
            .Include(x => x.Offers)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);

        var driverLocation = driverProfile is null
            ? null
            : await dbContext.DriverLiveLocations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.DriverId == driverProfile.UserId, cancellationToken);

        return rides.Select(x =>
        {
            var assignedToCurrentDriver = driverProfile is not null && x.DriverId == driverProfile.UserId;
            // The customer, an administrator and the assigned driver may see
            // the full destination.  A driver deciding whether to make an
            // offer receives only pickup navigation and a readable area name.
            var revealDestination = isAdmin || driverProfile is null || assignedToCurrentDriver;
            var offer = driverProfile is null
                ? null
                : x.Offers.Where(item => item.DriverId == driverProfile.UserId)
                    .OrderByDescending(item => item.CreatedAtUtc).FirstOrDefault();
            var pickupName = RideLocationText.DisplayName(x.PickupLabel, x.PickupAddress, "نقطة الانطلاق");
            var destinationName = revealDestination
                ? RideLocationText.DisplayName(x.DestinationLabel, x.DestinationAddress, "الوجهة")
                : RideLocationText.AreaName(x.DestinationLabel, x.DestinationAddress, "ضمن المنطقة المحددة");
            var pickupDistanceMeters = driverLocation is null
                ? (double?)null
                : HaversineMeters(driverLocation.Latitude, driverLocation.Longitude, x.PickupLatitude, x.PickupLongitude);

            return new
            {
                x.Id, x.CustomerId,
                customerName = revealDestination ? x.Customer.DisplayName : null,
                customerPhone = revealDestination ? x.Customer.PhoneNumber : null,
                x.DriverId,
                driverName = x.Driver?.DisplayName,
                x.Status, x.ServiceKindId, x.ServiceCatalogItemId,
                serviceKindCode = x.ServiceKind.Code, serviceKindNameAr = x.ServiceKind.NameAr,
                serviceCode = x.ServiceCatalogItem.Code, serviceNameAr = x.ServiceCatalogItem.NameAr,
                pickupDisplayName = pickupName,
                destinationDisplayName = destinationName,
                pickupAddress = pickupName,
                destinationAddress = destinationName,
                x.PickupLatitude, x.PickupLongitude,
                destinationLatitude = revealDestination ? x.DestinationLatitude : (double?)null,
                destinationLongitude = revealDestination ? x.DestinationLongitude : (double?)null,
                destinationAvailable = revealDestination,
                pickupDistanceMeters,
                driverLatitude = driverProfile is null ? (double?)null : driverLocation?.Latitude,
                driverLongitude = driverProfile is null ? (double?)null : driverLocation?.Longitude,
                driverLocationObservedAtUtc = driverProfile is null ? null : driverLocation?.ObservedAtUtc,
                x.CustomerPrice, x.ServiceFee, x.TotalAmount, x.DriverCommissionAmount, x.DriverShare, x.PlatformShare, x.CreatedAtUtc,
                driverOfferAmount = offer?.Amount,
                driverOfferExpiresAtUtc = offer?.ExpiresAtUtc,
                driverOfferStatus = offer?.Status
            };
        }).ToList();
    }

    private static double HaversineMeters(double firstLatitude, double firstLongitude,
        double secondLatitude, double secondLongitude)
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
