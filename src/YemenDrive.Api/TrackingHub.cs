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
    NearbyDriverSubscriptionStore nearbySubscriptions,
    ILogger<TrackingHub> logger,
    DriverLocationIngestionService locationIngestion,
    RideParticipantPresence? presence = null) : Hub
{
    private static readonly RideStatus[] TrackableRideStatuses =
    [
        RideStatus.DriverAssigned,
        RideStatus.DriverEnRoute,
        RideStatus.DriverArrived,
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

        presence?.Connect(Context.ConnectionId, userId);

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
        presence?.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task SetForeground(bool foreground)
    {
        presence?.SetForeground(Context.ConnectionId, foreground);
        return Task.CompletedTask;
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
        try { await locationIngestion.SaveAsync(driverId, sample); }
        catch (OperationCanceledException) {
            logger.LogWarning("Driver location ingestion timed out or the server stopped for {DriverId}.", driverId);
            throw new HubException("تعذر حفظ الموقع الآن؛ ستعاد المحاولة تلقائياً.");
        }
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
        RideStatus.DriverArrived,
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
