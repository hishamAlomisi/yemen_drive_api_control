using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Rides;

namespace YemenDrive.Api;

public sealed class DriverLocationIngestionService(YemenDriveDbContext db,
    IRideRealtimePublisher realtimePublisher, NearbyDriverSubscriptionStore nearbySubscriptions,
    IHubContext<TrackingHub> hub, IHostApplicationLifetime lifetime,
    ILogger<DriverLocationIngestionService> logger)
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> DriverLocks = new();
    private static readonly RideStatus[] TrackableRideStatuses = [RideStatus.DriverAssigned,
        RideStatus.DriverEnRoute, RideStatus.DriverArrived, RideStatus.InProgress, RideStatus.CancellationPending];
    public async Task SaveAsync(int driverId, DriverLocationHubRequest sample)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var gate = DriverLocks.GetOrAdd(driverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(deadline.Token);
        try { await SaveOrderedAsync(driverId, sample, deadline.Token); }
        finally { gate.Release(); }
    }
    private async Task SaveOrderedAsync(int driverId, DriverLocationHubRequest sample, CancellationToken token)
    {
        if (!double.IsFinite(sample.Latitude) || !double.IsFinite(sample.Longitude) ||
            sample.Latitude is < -90 or > 90 || sample.Longitude is < -180 or > 180 ||
            !double.IsFinite(sample.Accuracy) || sample.Accuracy is <= 0 or > 75 ||
            sample.Bearing is double bearing && (!double.IsFinite(bearing) || bearing is < 0 or >= 360) ||
            sample.Speed is double speed && (!double.IsFinite(speed) || speed is < 0 or > 75))
            throw new HubException("بيانات الموقع غير صالحة أو دقتها منخفضة.");

        var now = DateTime.UtcNow;
        var observedAtUtc = sample.ObservedAtUtc?.ToUniversalTime() ?? now;
        if (observedAtUtc < now.AddSeconds(-30) || observedAtUtc > now.AddSeconds(5))
            throw new HubException("عينة الموقع قديمة أو وقتها غير صالح.");

        var profile = await db.DriverProfiles.AsNoTracking()
            .Where(item => item.UserId == driverId && item.User.IsActive && item.User.Role == UserRole.Driver)
            .Select(item => new
            {
                item.IsAvailable,
                item.AcceptsNewRequests,
                item.IsApproved,
                item.ServiceKindId,
                item.ServiceCatalogItemId,
                name = item.User.DisplayName,
                photoUrl = item.PhotoUrl,
                vehicleModel = item.VehicleModel,
                plateNumber = item.PlateNumber,
                rating = db.DriverRatings.Where(rating => rating.DriverUserId == item.UserId)
                    .Select(rating => (decimal?)rating.Score).Average() ?? 0m,
                serviceCode = item.ServiceCatalogItem.Code
            })
            .SingleOrDefaultAsync(token);
        if (profile is null)
            throw new HubException("الحساب لا يملك ملف سائق نشطًا.");

        var activeRides = db.Rides.AsNoTracking()
            .Where(ride => ride.DriverId == driverId && ride.ServiceEndedAtUtc == null && TrackableRideStatuses.Contains(ride.Status));
        if (sample.RideId is int requestedRideId)
            activeRides = activeRides.Where(ride => ride.Id == requestedRideId);
        var activeRide = await activeRides
            .OrderByDescending(ride => ride.UpdatedAtUtc)
            .Select(ride => new { ride.Id, ride.CustomerId, ride.Status })
            .FirstOrDefaultAsync(token);
        if (sample.RideId is not null && activeRide is null)
            throw new HubException("الرحلة غير مسندة إليك أو لم تعد نشطة.");
        var location = await db.DriverLiveLocations
            .SingleOrDefaultAsync(item => item.DriverId == driverId, token);
        if (location is not null && observedAtUtc <= location.ObservedAtUtc)
            return;
        if (location is not null &&
            observedAtUtc - location.ObservedAtUtc < TimeSpan.FromMilliseconds(750))
            return;

        if (location is not null && HaversineMeters(location.Latitude, location.Longitude, sample.Latitude, sample.Longitude) >
            75 * (observedAtUtc - location.ObservedAtUtc).TotalSeconds + Math.Max(50, sample.Accuracy * 2))
            throw new HubException("قفزة موقع السائق لا تتوافق مع سرعة حركة ممكنة.");

        if (location is null)
        {
            location = new DriverLiveLocation { DriverId = driverId };
            db.DriverLiveLocations.Add(location);
        }
        location.Latitude = sample.Latitude;
        location.Longitude = sample.Longitude;
        location.Bearing = sample.Bearing;
        location.Speed = sample.Speed;
        location.IsOnline = true;
        location.ObservedAtUtc = observedAtUtc;
        location.UpdatedAtUtc = now;
        if (activeRide?.Status == RideStatus.InProgress)
            db.LocationUpdates.Add(new LocationUpdate { RideId = activeRide.Id, ActorId = driverId,
                Latitude = sample.Latitude, Longitude = sample.Longitude, Bearing = sample.Bearing,
                Speed = sample.Speed, Accuracy = sample.Accuracy, ObservedAtUtc = observedAtUtc });
        await db.SaveChangesAsync(token);

        var realtimeLocation = new DriverLocationRealtimeEvent(
                driverId,
                activeRide?.Id,
                activeRide?.CustomerId,
                sample.Latitude,
                sample.Longitude,
                sample.Bearing,
                sample.Speed,
                sample.Accuracy,
                observedAtUtc,
                now,
                activeRide?.Status);
        if (activeRide is not null)
            await realtimePublisher.PublishDriverLocationAsync(
                realtimeLocation, token);

        var deliveries = nearbySubscriptions.Evaluate(
            profile.ServiceKindId,
            profile.ServiceCatalogItemId,
            driverId,
            sample.Latitude,
            sample.Longitude,
            profile.IsAvailable && profile.AcceptsNewRequests && profile.IsApproved && activeRide is null);
        await Parallel.ForEachAsync(
            deliveries,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (delivery, _) =>
            {
                try
                {
                    if (!delivery.IsAvailable)
                    {
                        await hub.Clients.Client(delivery.ConnectionId).SendAsync(
                            "nearbyDriverAvailabilityChanged",
                            new { driverId, isAvailable = false },
                            CancellationToken.None);
                        return;
                    }
                    await hub.Clients.Client(delivery.ConnectionId).SendAsync(
                        "nearbyDriverLocationUpdated",
                        new
                        {
                            driverId,
                            name = profile.name ?? "سائق يمن درايف",
                            photoUrl = profile.photoUrl,
                            vehicleModel = profile.vehicleModel,
                            plateNumber = profile.plateNumber,
                            rating = profile.rating,
                            serviceCode = profile.serviceCode,
                            latitude = sample.Latitude,
                            longitude = sample.Longitude,
                            bearing = sample.Bearing,
                            observedAtUtc,
                            distanceMeters = delivery.DistanceMeters,
                            isAvailable = true
                        },
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception,
                        "Could not update a nearby-driver map for driver {DriverId}.", driverId);
                }
            });
    }
    private static double HaversineMeters(
        double firstLatitude,
        double firstLongitude,
        double secondLatitude,
        double secondLongitude)
    {
        const double earthRadiusMeters = 6_371_000d;
        var latitudeDelta = DegreesToRadians(secondLatitude - firstLatitude);
        var longitudeDelta = DegreesToRadians(secondLongitude - firstLongitude);
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
                Math.Cos(DegreesToRadians(firstLatitude)) * Math.Cos(DegreesToRadians(secondLatitude)) *
                Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        a = Math.Clamp(a, 0d, 1d);
        return 2d * earthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

}
