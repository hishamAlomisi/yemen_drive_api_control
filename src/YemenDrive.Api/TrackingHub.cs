using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Rides;
using YemenDrive.Shared.Api;

namespace YemenDrive.Api;

public sealed class TrackingHub(
    YemenDriveDbContext db,
    TrackingHubAccessPolicy accessPolicy,
    IRideRealtimePublisher realtimePublisher,
    NearbyDriverSubscriptionStore nearbySubscriptions,
    ILogger<TrackingHub> logger) : Hub
{
    private static readonly RideStatus[] TrackableRideStatuses =
    [
        RideStatus.DriverAssigned,
        RideStatus.DriverEnRoute,
        RideStatus.InProgress,
        RideStatus.CancellationPending
    ];

    public override async Task OnConnectedAsync()
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            UserGroup(userId),
            Context.ConnectionAborted);
        if (Context.GetHttpContext()?.Items.TryGetValue(
                AuthSessionAuthenticationMiddleware.SessionItem,
                out var value) == true && value is AuthenticatedSession session)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                FamilyGroup(session.FamilyId),
                Context.ConnectionAborted);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        nearbySubscriptions.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinRide(int rideId)
    {
        if (rideId <= 0 || !TryGetAuthenticatedUserId(out var userId))
            throw new HubException("تعذر التحقق من صلاحية الاتصال.");

        if (!await accessPolicy.CanJoinRideAsync(userId, rideId, Context.ConnectionAborted))
            throw new HubException("ليس لديك صلاحية متابعة هذه الرحلة.");

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RideGroup(rideId),
            Context.ConnectionAborted);
    }

    public Task LeaveRide(int rideId)
    {
        if (rideId <= 0)
            throw new HubException("رقم الرحلة غير صالح.");
        return Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            RideGroup(rideId),
            Context.ConnectionAborted);
    }

    public async Task JoinOperations()
    {
        if (!TryGetAuthenticatedUserId(out var userId) ||
            !await accessPolicy.IsActiveAdministratorAsync(userId, Context.ConnectionAborted))
            throw new HubException("هذه القناة مخصصة لحساب إدارة نشط.");

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            OperationsGroup,
            Context.ConnectionAborted);
    }

    public async Task JoinNearbyDrivers(NearbyDriversHubRequest request)
    {
        if (!TryGetAuthenticatedUserId(out var userId) ||
            !await accessPolicy.IsActiveCustomerAsync(userId, Context.ConnectionAborted))
            throw new HubException("هذه القناة مخصصة لحساب عميل نشط.");
        if (!double.IsFinite(request.PickupLatitude) || !double.IsFinite(request.PickupLongitude) ||
            request.PickupLatitude is < -90 or > 90 || request.PickupLongitude is < -180 or > 180 ||
            request.InitialDriverIds is { Length: > 50 })
            throw new HubException("بيانات البحث عن السائقين غير صالحة.");
        if (request.ServiceCatalogItemId is int catalogId &&
            !await db.ServiceCatalogItems.AsNoTracking().AnyAsync(
                item => item.Id == catalogId && item.IsActive &&
                        (request.ServiceKindId == null || item.ServiceKindId == request.ServiceKindId),
                Context.ConnectionAborted))
            throw new HubException("الخدمة المحددة غير متاحة.");

        nearbySubscriptions.Replace(
            Context.ConnectionId,
            new NearbyDriverSubscriptionRequest(
                request.ServiceKindId,
                request.ServiceCatalogItemId,
                request.PickupLatitude,
                request.PickupLongitude,
                request.RadiusMeters,
                request.InitialDriverIds ?? []));
    }

    public Task LeaveNearbyDrivers()
    {
        nearbySubscriptions.Remove(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public async Task PublishDriverLocation(DriverLocationHubRequest sample)
    {
        if (!TryGetAuthenticatedUserId(out var driverId))
            throw new HubException("يجب تسجيل الدخول لإرسال موقع السائق.");
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
            .Where(item => item.UserId == driverId && item.User.IsActive)
            .Select(item => new
            {
                item.IsAvailable,
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
            .SingleOrDefaultAsync(Context.ConnectionAborted);
        if (profile is null)
            throw new HubException("الحساب لا يملك ملف سائق نشطًا.");

        var activeRides = db.Rides.AsNoTracking()
            .Where(ride => ride.DriverId == driverId && TrackableRideStatuses.Contains(ride.Status));
        if (sample.RideId is int requestedRideId)
            activeRides = activeRides.Where(ride => ride.Id == requestedRideId);
        var activeRide = await activeRides
            .OrderByDescending(ride => ride.UpdatedAtUtc)
            .Select(ride => new { ride.Id, ride.CustomerId, ride.Status })
            .FirstOrDefaultAsync(Context.ConnectionAborted);
        if (sample.RideId is not null && activeRide is null)
            throw new HubException("الرحلة غير مسندة إليك أو لم تعد نشطة.");
        var location = await db.DriverLiveLocations
            .SingleOrDefaultAsync(item => item.DriverId == driverId, Context.ConnectionAborted);
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
        await db.SaveChangesAsync(Context.ConnectionAborted);

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
                realtimeLocation, Context.ConnectionAborted);

        var deliveries = nearbySubscriptions.Evaluate(
            profile.ServiceKindId,
            profile.ServiceCatalogItemId,
            driverId,
            sample.Latitude,
            sample.Longitude,
            profile.IsAvailable && activeRide is null);
        await Parallel.ForEachAsync(
            deliveries,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (delivery, _) =>
            {
                try
                {
                    if (!delivery.IsAvailable)
                    {
                        await Clients.Client(delivery.ConnectionId).SendAsync(
                            "nearbyDriverAvailabilityChanged",
                            new { driverId, isAvailable = false },
                            CancellationToken.None);
                        return;
                    }
                    await Clients.Client(delivery.ConnectionId).SendAsync(
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

    /// <summary>
    /// Accepts a GPS sample from the customer who owns an assigned or active
    /// ride. The actor is always derived from the authenticated Hub session;
    /// callers cannot choose a customer, driver, or another ride participant.
    /// </summary>
    public async Task PublishPassengerLocation(PassengerLocationHubRequest sample)
    {
        if (!TryGetAuthenticatedUserId(out var customerId))
            throw new HubException("يجب تسجيل الدخول لإرسال موقع العميل.");
        if (sample.RideId <= 0 ||
            !double.IsFinite(sample.Latitude) || !double.IsFinite(sample.Longitude) ||
            sample.Latitude is < -90 or > 90 || sample.Longitude is < -180 or > 180 ||
            !double.IsFinite(sample.Accuracy) || sample.Accuracy is <= 0 or > 75 ||
            sample.Bearing is double bearing && (!double.IsFinite(bearing) || bearing is < 0 or >= 360) ||
            sample.Speed is double speed && (!double.IsFinite(speed) || speed is < 0 or > 75))
            throw new HubException("بيانات موقع العميل غير صالحة أو دقتها منخفضة.");

        var now = DateTime.UtcNow;
        var observedAtUtc = sample.ObservedAtUtc?.ToUniversalTime() ?? now;
        if (observedAtUtc < now.AddSeconds(-30) || observedAtUtc > now.AddSeconds(5))
            throw new HubException("عينة موقع العميل قديمة أو وقتها غير صالح.");

        // Do not trust any identity or recipient supplied by the client. A
        // customer can publish only for their own currently trackable ride.
        var ride = await db.Rides.AsNoTracking()
            .Where(item => item.Id == sample.RideId &&
                           item.CustomerId == customerId &&
                           item.DriverId != null &&
                           TrackableRideStatuses.Contains(item.Status))
            .Select(item => new { item.DriverId, item.Status })
            .SingleOrDefaultAsync(Context.ConnectionAborted);
        if (ride is null || ride.DriverId is not int driverId)
            throw new HubException("الرحلة غير مخصصة لك أو لم تعد قابلة للتتبع.");

        // Keep the existing LocationUpdates history used by safety and
        // cancellation workflows, while rejecting replays, floods, and
        // physically implausible coordinate jumps.
        var previous = await db.LocationUpdates.AsNoTracking()
            .Where(item => item.RideId == sample.RideId && item.ActorId == customerId)
            .OrderByDescending(item => item.ObservedAtUtc)
            .Select(item => new { item.Latitude, item.Longitude, item.ObservedAtUtc })
            .FirstOrDefaultAsync(Context.ConnectionAborted);
        if (previous is not null)
        {
            if (observedAtUtc <= previous.ObservedAtUtc ||
                observedAtUtc - previous.ObservedAtUtc < TimeSpan.FromMilliseconds(750))
                return;

            var travelledMeters = HaversineMeters(
                previous.Latitude,
                previous.Longitude,
                sample.Latitude,
                sample.Longitude);
            var maximumPlausibleMeters = 75d *
                (observedAtUtc - previous.ObservedAtUtc).TotalSeconds +
                Math.Max(50d, sample.Accuracy * 2d);
            if (!double.IsFinite(travelledMeters) || travelledMeters > maximumPlausibleMeters)
                throw new HubException("قفزة موقع العميل لا تتوافق مع سرعة حركة ممكنة.");
        }

        db.LocationUpdates.Add(new LocationUpdate
        {
            RideId = sample.RideId,
            ActorId = customerId,
            Latitude = sample.Latitude,
            Longitude = sample.Longitude,
            Bearing = sample.Bearing,
            Speed = sample.Speed,
            ObservedAtUtc = observedAtUtc
        });
        await db.SaveChangesAsync(Context.ConnectionAborted);

        var payload = new
        {
            rideId = sample.RideId,
            customerId,
            latitude = sample.Latitude,
            longitude = sample.Longitude,
            bearing = sample.Bearing,
            speed = sample.Speed,
            accuracy = sample.Accuracy,
            observedAtUtc,
            receivedAtUtc = now,
            rideStatus = (int)ride.Status
        };

        // Customer coordinates are intentionally excluded from the public
        // share-link Hub. They are sent only to the assigned driver and
        // authorized operations connections.
        await TrySendToGroupAsync(
            UserGroup(driverId),
            "passengerLocationUpdated",
            payload,
            "Could not publish passengerLocationUpdated to driver for ride {RideId}.",
            sample.RideId);
        await TrySendToGroupAsync(
            OperationsGroup,
            "passengerLocationUpdated",
            payload,
            "Could not publish passengerLocationUpdated to operations for ride {RideId}.",
            sample.RideId);
    }

    internal static string UserGroup(int userId) => $"user:{userId}";
    internal static string RideGroup(int rideId) => $"ride:{rideId}";
    internal static string FamilyGroup(Guid familyId) => $"session-family:{familyId:N}";
    internal const string OperationsGroup = "admin:operations";

    private async Task TrySendToGroupAsync(
        string group,
        string eventName,
        object payload,
        string logMessage,
        int rideId)
    {
        try
        {
            await Clients.Group(group).SendCoreAsync(eventName, [payload], CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, logMessage, rideId);
        }
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

    private bool TryGetAuthenticatedUserId(out int userId)
    {
        userId = 0;
        return Context.GetHttpContext()?.Items.TryGetValue(
                   AuthSessionAuthenticationMiddleware.UserIdItem,
                   out var value) == true &&
               value is int authenticatedUserId &&
               (userId = authenticatedUserId) > 0;
    }
}

public sealed record DriverLocationHubRequest(
    int? RideId,
    double Latitude,
    double Longitude,
    double? Bearing,
    double? Speed,
    double Accuracy,
    DateTime? ObservedAtUtc);

public sealed record PassengerLocationHubRequest(
    int RideId,
    double Latitude,
    double Longitude,
    double? Bearing,
    double? Speed,
    double Accuracy,
    DateTime? ObservedAtUtc);

public sealed record NearbyDriversHubRequest(
    int? ServiceKindId,
    int? ServiceCatalogItemId,
    double PickupLatitude,
    double PickupLongitude,
    int RadiusMeters,
    int[]? InitialDriverIds);

public sealed class TrackingHubAccessPolicy(YemenDriveDbContext db)
{
    private static readonly RideStatus[] JoinableRideStatuses =
    [
        RideStatus.Searching,
        RideStatus.Negotiating,
        RideStatus.DriverAssigned,
        RideStatus.DriverEnRoute,
        RideStatus.InProgress,
        RideStatus.CancellationPending
    ];

    public async Task<bool> CanJoinRideAsync(
        int userId,
        int rideId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0 || rideId <= 0) return false;
        var ride = await db.Rides.AsNoTracking()
            .Where(item => item.Id == rideId && JoinableRideStatuses.Contains(item.Status))
            .Select(item => new { item.CustomerId, item.DriverId })
            .SingleOrDefaultAsync(cancellationToken);
        if (ride is null) return false;
        if (ride.CustomerId == userId || ride.DriverId == userId) return true;
        return await IsActiveAdministratorAsync(userId, cancellationToken);
    }

    public Task<bool> IsActiveAdministratorAsync(int userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(
            item => item.Id == userId &&
                    item.Role == UserRole.Admin &&
                    item.IsActive,
            cancellationToken);

    public Task<bool> IsActiveCustomerAsync(int userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(
            item => item.Id == userId &&
                    item.Role == UserRole.Customer &&
                    item.IsActive,
            cancellationToken);
}
