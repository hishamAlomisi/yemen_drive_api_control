using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using RideEntity = YemenDrive.Database.Entities.Ride;
using RideOfferEntity = YemenDrive.Database.Entities.RideOffer;

namespace YemenDrive.Application.Rides;

internal static class RideSearchNotificationDispatcher
{
    public static string RideData(int rideId) => $"{{\"action\":\"open_ride_offer\",\"rideId\":{rideId}}}";

    public static async Task<IReadOnlyCollection<RideAvailableRecipient>> NotifyAvailableDriversAsync(
        YemenDriveDbContext db,
        RideEntity ride,
        CancellationToken token)
    {
        var radiusMeters = RideDriverProximity.RadiusMeters(ride, DateTime.UtcNow);
        var locationFreshAfter = DateTime.UtcNow.AddMinutes(-5);
        var candidates = (await db.DriverProfiles.AsNoTracking()
            .Where(profile => profile.User.IsActive && profile.IsAvailable &&
                              profile.ServiceKindId == ride.ServiceKindId &&
                              profile.ServiceCatalogItemId == ride.ServiceCatalogItemId &&
                              !db.Rides.Any(activeRide =>
                                  activeRide.DriverId == profile.UserId &&
                                  (activeRide.Status == RideStatus.DriverAssigned ||
                                   activeRide.Status == RideStatus.DriverEnRoute ||
                                   activeRide.Status == RideStatus.InProgress ||
                                   activeRide.Status == RideStatus.CancellationPending)))
            .Join(db.DriverLiveLocations.AsNoTracking()
                    .Where(location => location.IsOnline &&
                                       location.ObservedAtUtc >= locationFreshAfter),
                profile => profile.UserId,
                location => location.DriverId,
                (profile, location) => new
                {
                    profile.UserId,
                    location.Latitude,
                    location.Longitude,
                    location.ObservedAtUtc
                })
            .Take(200)
            .ToListAsync(token))
            .Select(candidate => new
            {
                candidate.UserId,
                candidate.Latitude,
                candidate.Longitude,
                candidate.ObservedAtUtc,
                PickupDistanceMeters = RideDriverProximity.HaversineMeters(
                    ride.PickupLatitude,
                    ride.PickupLongitude,
                    candidate.Latitude,
                    candidate.Longitude)
            })
            .Where(candidate => candidate.PickupDistanceMeters <= radiusMeters)
            .GroupBy(candidate => candidate.UserId)
            .Select(group => group.First())
            .ToList();

        var driverIds = candidates.Select(candidate => candidate.UserId).ToArray();
        var kind = await db.ServiceKinds.AsNoTracking()
            .Where(item => item.Id == ride.ServiceKindId)
            .Select(item => new { item.Code, item.NameAr })
            .SingleOrDefaultAsync(token);
        var service = await db.ServiceCatalogItems.AsNoTracking()
            .Where(item => item.Id == ride.ServiceCatalogItemId)
            .Select(item => new { item.Code, item.NameAr })
            .SingleOrDefaultAsync(token);
        var latestOffers = driverIds.Length == 0
            ? new Dictionary<int, RideOfferEntity>()
            : (await db.RideOffers.AsNoTracking()
                .Where(offer => offer.RideId == ride.Id && driverIds.Contains(offer.DriverId))
                .OrderByDescending(offer => offer.CreatedAtUtc)
                .ToListAsync(token))
                .GroupBy(offer => offer.DriverId)
                .ToDictionary(group => group.Key, group => group.First());

        var routeSummary = RideLocationText.RouteSummary(
            ride.PickupLabel,
            ride.PickupAddress,
            ride.DestinationLabel,
            ride.DestinationAddress);
        var recipients = new List<RideAvailableRecipient>(candidates.Count);
        foreach (var candidate in candidates)
        {
            db.Notifications.Add(new Notification
            {
                UserId = candidate.UserId,
                Type = NotificationType.RideOffer,
                Title = "طلب رحلة جديد",
                Body = $"يوجد طلب جديد {routeSummary}.",
                DataJson = RideData(ride.Id)
            });

            latestOffers.TryGetValue(candidate.UserId, out var latestOffer);
            var pickupName = RideLocationText.DisplayName(
                ride.PickupLabel, ride.PickupAddress, "نقطة الانطلاق");
            var destinationName = RideLocationText.AreaName(
                ride.DestinationLabel, ride.DestinationAddress, "ضمن المنطقة المحددة");
            var rideSnapshot = new Dictionary<string, object?>
            {
                ["id"] = ride.Id,
                ["customerId"] = ride.CustomerId,
                ["customerName"] = null,
                ["customerPhone"] = null,
                ["driverId"] = ride.DriverId,
                ["driverName"] = null,
                ["status"] = (int)ride.Status,
                ["serviceKindId"] = ride.ServiceKindId,
                ["serviceCatalogItemId"] = ride.ServiceCatalogItemId,
                ["serviceKindCode"] = kind?.Code,
                ["serviceKindNameAr"] = kind?.NameAr,
                ["serviceCode"] = service?.Code,
                ["serviceNameAr"] = service?.NameAr,
                ["pickupDisplayName"] = pickupName,
                ["destinationDisplayName"] = destinationName,
                ["pickupAddress"] = pickupName,
                ["destinationAddress"] = destinationName,
                ["pickupLatitude"] = ride.PickupLatitude,
                ["pickupLongitude"] = ride.PickupLongitude,
                ["destinationLatitude"] = null,
                ["destinationLongitude"] = null,
                ["destinationAvailable"] = false,
                ["pickupDistanceMeters"] = candidate.PickupDistanceMeters,
                ["driverLatitude"] = candidate.Latitude,
                ["driverLongitude"] = candidate.Longitude,
                ["driverLocationObservedAtUtc"] = candidate.ObservedAtUtc,
                ["customerPrice"] = ride.CustomerPrice,
                ["serviceFee"] = ride.ServiceFee,
                ["totalAmount"] = ride.TotalAmount,
                ["driverCommissionAmount"] = ride.DriverCommissionAmount,
                ["driverShare"] = ride.DriverShare,
                ["platformShare"] = ride.PlatformShare,
                ["createdAtUtc"] = ride.CreatedAtUtc,
                ["customerPaymentEnabled"] = ride.CustomerPaymentEnabled,
                ["paymentCompleted"] = false,
                ["driverOfferAmount"] = latestOffer?.Amount,
                ["driverOfferExpiresAtUtc"] = latestOffer?.ExpiresAtUtc,
                ["driverOfferStatus"] = latestOffer is null ? null : (int)latestOffer.Status,
                ["cashPaymentRequestId"] = null,
                ["cashPaymentRequestStatus"] = null
            };
            recipients.Add(new RideAvailableRecipient(candidate.UserId, rideSnapshot));
        }

        return recipients;
    }
}
