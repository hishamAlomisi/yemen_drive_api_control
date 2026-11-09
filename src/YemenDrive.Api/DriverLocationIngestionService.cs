using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Rides;
using YemenDrive.Api.Tracking;

namespace YemenDrive.Api;

public sealed class DriverLocationIngestionService(YemenDriveDbContext db,
    IRideRealtimePublisher realtimePublisher, NearbyDriverSubscriptionStore nearbySubscriptions,
    IHubContext<TrackingHub> hub, IHostApplicationLifetime lifetime,
    ILogger<DriverLocationIngestionService> logger,
    DriverLocationVisualQueue? visualQueue = null)
{
    // Fixed stripes bound process memory; the SQL lock retains exact per-driver
    // archive serialization. Different drivers may share a stripe under high load.
    private static readonly SemaphoreSlim[] DriverLocks = Enumerable.Range(0, 1024).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly RideStatus[] TrackableRideStatuses = [RideStatus.DriverAssigned,
        RideStatus.DriverEnRoute, RideStatus.DriverArrived, RideStatus.InProgress, RideStatus.CancellationPending];
    public async Task SaveAsync(int driverId, DriverLocationHubRequest sample)
    {
        await SaveCoreAsync(driverId, sample, true);
    }

    internal Task<bool> SaveWithoutArchiveAsync(int driverId, DriverLocationHubRequest sample)
        => SaveCoreAsync(driverId, sample, false);

    // Only the permission-checked administrator model calls this path. Test
    // points share durable storage and live fan-out, but retain their source.
    internal Task<bool> SaveAdministratorAsync(int driverId, DriverLocationHubRequest sample, string source, DateTime? expected, byte[]? expectedRideVersion = null)
        => SaveCoreAsync(driverId, sample, true, source, expected, expectedRideVersion);

    private async Task<bool> SaveCoreAsync(int driverId, DriverLocationHubRequest sample, bool saveArchive, string? adminSource = null, DateTime? expected = null, byte[]? expectedRideVersion = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var gate = DriverLocks[(int)((uint)driverId % (uint)DriverLocks.Length)];
        await gate.WaitAsync(deadline.Token);
        try { return await SaveOrderedAsync(driverId, sample, saveArchive, deadline.Token, adminSource, expected, expectedRideVersion); }
        finally { gate.Release(); }
    }
    private async Task<bool> SaveOrderedAsync(int driverId, DriverLocationHubRequest sample, bool saveArchive, CancellationToken token, string? adminSource, DateTime? expected, byte[]? expectedRideVersion)
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
        // The legacy single-sample endpoint can overlap a retry through the
        // durable batch endpoint. Both writers serialize archive changes using
        // the same database lock, including requests served by another process.
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var resource = $"driver-route:{driverId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51002, 'Route sample lock timed out', 1;", token);
        if (expectedRideVersion is not null)
        {
            // A provider lookup may outlive a destination/phase change. Hold
            // this ride read through commit so a step cannot use an old route.
            var currentRide = await db.Rides.FromSqlInterpolated($"SELECT * FROM [Rides] WITH (HOLDLOCK) WHERE [Id] = {sample.RideId}")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (currentRide is null || currentRide.DriverId != driverId || currentRide.ServiceEndedAtUtc != null ||
                !currentRide.RowVersion.SequenceEqual(expectedRideVersion) ||
                currentRide.Status is not (RideStatus.DriverAssigned or RideStatus.DriverEnRoute or RideStatus.InProgress))
                throw new HubException("تغيرت حالة الرحلة أو وجهتها أثناء تجهيز الحركة؛ حدّث الموقع وحاول مجدداً.");
        }
        var location = await db.DriverLiveLocations
            .SingleOrDefaultAsync(item => item.DriverId == driverId, token);
        if (adminSource is not null)
        {
            if (expected is DateTime expectedAt && location?.ObservedAtUtc != expectedAt)
                throw new HubException("تغير موقع السائق أثناء تجهيز الحركة؛ حدّث الموقع وحاول مجدداً.");
            // Separate clicks always persist, even inside the GPS throttle.
            observedAtUtc = location is not null && location.ObservedAtUtc >= now ? location.ObservedAtUtc.AddTicks(1) : now;
        }
        if (location is not null && observedAtUtc <= location.ObservedAtUtc)
            return false;
        if (adminSource is null && location is not null &&
            observedAtUtc - location.ObservedAtUtc < TimeSpan.FromMilliseconds(750))
            return false;

        if (adminSource is null && location is not null && HaversineMeters(location.Latitude, location.Longitude, sample.Latitude, sample.Longitude) >
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
        if (saveArchive && activeRide is not null)
        {
            var savedPoint = await db.LocationUpdates.AsNoTracking().FirstOrDefaultAsync(x => x.RideId == activeRide.Id &&
                x.ActorId == driverId && x.ObservedAtUtc == observedAtUtc, token);
            if (savedPoint is not null && (savedPoint.Latitude != sample.Latitude || savedPoint.Longitude != sample.Longitude))
                throw new HubException("تعارضت الإحداثيات مع عينة محفوظة بالوقت نفسه.");
            if (savedPoint is null)
                db.LocationUpdates.Add(new LocationUpdate { RideId = activeRide.Id, ActorId = driverId,
                    Latitude = sample.Latitude, Longitude = sample.Longitude, Bearing = sample.Bearing,
                    Speed = sample.Speed, Accuracy = sample.Accuracy, ObservedAtUtc = observedAtUtc, Source = adminSource ?? "gps" });
        }
        await db.SaveChangesAsync(token);
        // Never hold the route transaction while waiting for viewers/network.
        await transaction.CommitAsync(token);

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
        if (visualQueue?.Enabled == true)
        {
            visualQueue.Enqueue(new(realtimeLocation, new(profile.ServiceKindId, profile.ServiceCatalogItemId,
                profile.IsAvailable && profile.AcceptsNewRequests && profile.IsApproved, profile.name, profile.photoUrl,
                profile.vehicleModel, profile.plateNumber, profile.rating, profile.serviceCode)));
            return true;
        }
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
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = token },
            async (delivery, ct) =>
            {
                try
                {
                    if (!delivery.IsAvailable)
                    {
                        await hub.Clients.Client(delivery.ConnectionId).SendAsync(
                            "nearbyDriverAvailabilityChanged",
                            new { driverId, isAvailable = false },
                            ct);
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
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogDebug(exception,
                        "Could not update a nearby-driver map for driver {DriverId}.", driverId);
                }
            });
        return true;
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
