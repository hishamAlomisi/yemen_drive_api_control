using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Rides;
using YemenDrive.Shared.Api;
using YemenDrive.Api.Realtime;

namespace YemenDrive.Api;

public sealed partial class TrackingHub(
    YemenDriveDbContext db,
    TrackingHubAccessPolicy accessPolicy,
    NearbyDriverSubscriptionStore nearbySubscriptions,
    ILogger<TrackingHub> logger,
    DriverLocationIngestionService locationIngestion,
    RideParticipantPresence? presence = null,
    RealtimeStateRecovery? stateRecovery = null,
    Tracking.RouteSampleBatchService? routeSamples = null,
    Tracking.RideMapWatchStore? mapWatches = null) : Hub
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
        await Groups.AddToGroupAsync(Context.ConnectionId, "state:authenticated", Context.ConnectionAborted);
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
        mapWatches?.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task SetForeground(bool foreground)
    {
        presence?.SetForeground(Context.ConnectionId, foreground);
        if (!foreground) mapWatches?.Remove(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public Task<StateReplayPage> ReplayStateChanges(Dictionary<string, long>? versions = null, string? afterStream = null)
    {
        if (!TryGetAuthenticatedUserId(out var userId)) throw new HubException("يجب تسجيل الدخول لاستعادة الحالة.");
        return stateRecovery?.ReadAsync(userId, versions, Context.ConnectionAborted, afterStream)
            ?? Task.FromResult(new StateReplayPage(false, [], false));
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
        var publication = await new PassengerLocationIngestionService(db)
            .SaveAsync(customerId, sample, Context.ConnectionAborted);
        await PassengerLocationIngestionService.PublishAsync(Clients, publication, logger, Context.ConnectionAborted);
    }
    internal static string UserGroup(int userId) => $"user:{userId}";
    internal static string RideGroup(int rideId) => $"ride:{rideId}";
    internal static string FamilyGroup(Guid familyId) => $"session-family:{familyId:N}";
    internal const string OperationsGroup = "admin:operations";

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
