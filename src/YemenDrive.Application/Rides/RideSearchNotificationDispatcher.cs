using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using RideEntity = YemenDrive.Database.Entities.Ride;
<<<<<<< HEAD
using RideOfferEntity = YemenDrive.Database.Entities.RideOffer;
=======
>>>>>>> origin/main

namespace YemenDrive.Application.Rides;

internal static class RideSearchNotificationDispatcher
{
<<<<<<< HEAD
    public static string RideData(int rideId) => $"{{\"action\":\"open_ride_offer\",\"rideId\":{rideId}}}";

    public static async Task<IReadOnlyCollection<RideAvailableRecipient>> NotifyAvailableDriversAsync(
=======
    public static string RideData(int rideId) => $"{{\"rideId\":{rideId}}}";

    public static async Task<int> NotifyAvailableDriversAsync(
>>>>>>> origin/main
        YemenDriveDbContext db,
        RideEntity ride,
        CancellationToken token)
    {
        var radiusMeters = RideDriverProximity.RadiusMeters(ride, DateTime.UtcNow);
        var locationFreshAfter = DateTime.UtcNow.AddMinutes(-5);
<<<<<<< HEAD
        var candidates = (await db.DriverProfiles.AsNoTracking()
=======
        var driverIds = (await db.DriverProfiles.AsNoTracking()
>>>>>>> origin/main
            .Where(profile => profile.User.IsActive && profile.IsAvailable &&
                              profile.ServiceKindId == ride.ServiceKindId &&
                              profile.ServiceCatalogItemId == ride.ServiceCatalogItemId &&
                              !db.Rides.Any(activeRide =>
                                  activeRide.DriverId == profile.UserId &&
                                  (activeRide.Status == RideStatus.DriverAssigned ||
                                   activeRide.Status == RideStatus.DriverEnRoute ||
<<<<<<< HEAD
                                   activeRide.Status == RideStatus.InProgress ||
                                   activeRide.Status == RideStatus.CancellationPending)))
=======
                                   activeRide.Status == RideStatus.InProgress)))
>>>>>>> origin/main
            .Join(db.DriverLiveLocations.AsNoTracking()
                    .Where(location => location.IsOnline &&
                                       location.ObservedAtUtc >= locationFreshAfter),
                profile => profile.UserId,
                location => location.DriverId,
                (profile, location) => new
                {
                    profile.UserId,
                    location.Latitude,
<<<<<<< HEAD
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

=======
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

>>>>>>> origin/main
        var routeSummary = RideLocationText.RouteSummary(
            ride.PickupLabel,
            ride.PickupAddress,
            ride.DestinationLabel,
            ride.DestinationAddress);
<<<<<<< HEAD
        var recipients = new List<RideAvailableRecipient>(candidates.Count);
        foreach (var candidate in candidates)
        {
            db.Notifications.Add(new Notification
            {
                UserId = candidate.UserId,
=======
        foreach (var driverId in driverIds)
        {
            db.Notifications.Add(new Notification
            {
                UserId = driverId,
>>>>>>> origin/main
                Type = NotificationType.RideOffer,
                Title = "طلب رحلة جديد",
                Body = $"يوجد طلب جديد {routeSummary}.",
                DataJson = RideData(ride.Id)
            });
<<<<<<< HEAD

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
=======
        }

        return driverIds.Count;
>>>>>>> origin/main
    }
}
