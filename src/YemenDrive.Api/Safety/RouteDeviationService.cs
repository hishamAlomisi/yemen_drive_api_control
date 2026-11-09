using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using YemenDrive.Application.Maps;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using Ride = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Api.Safety;

public sealed record RouteDeviationStatus(
    int RideId, bool Available, bool IsDeviating, double? DistanceMeters, string? Direction,
    double? DirectionDegrees, DateTime? ObservedAtUtc, int RouteVersion, string ReasonCode,
    string Message, double? AccuracyMeters = null, bool Accepted = false, int? IncidentId = null,
    bool Duplicate = false, double ThresholdMeters = RouteDeviationGeometry.ThresholdMeters);

public sealed class RouteDeviationService(YemenDriveDbContext db, GoogleMapsGateway maps,
    IRideRealtimePublisher? realtime = null, TimeProvider? timeProvider = null,
    ILogger<RouteDeviationService>? logger = null)
{
    private const string IncidentKind = "route_deviation";
    private static readonly MemoryCache BaselineFailures = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private static readonly SemaphoreSlim[] BaselineLocks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private DateTime Now => (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public async Task<RouteDeviationStatus> GetAsync(int rideId, int customerId, CancellationToken token)
    {
        var ride = await LoadAsync(rideId, customerId, token);
        ride = await EnsureBaselineAsync(ride, token);
        RequireOwner(ride, customerId);
        return await EvaluateAsync(ride, token);
    }

    public async Task<RouteDeviationStatus> ReportAsync(int rideId, int customerId, CancellationToken token)
    {
        var snapshot = await LoadAsync(rideId, customerId, token);
        await EnsureBaselineAsync(snapshot, token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Use the same driver lock as GPS and batch ingestion, then lock the
        // ride. Recheck both destination/version and location before committing.
        var resource = $"driver-route:{snapshot.DriverId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51002, 'Route safety lock timed out', 1;", token);
        var ride = await db.Rides.FromSqlInterpolated($"SELECT * FROM [Rides] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {rideId}")
            .AsNoTracking().SingleOrDefaultAsync(token) ?? throw Missing();
        RequireOwner(ride, customerId);
        var status = await EvaluateAsync(ride, token);
        if (!status.Available || !status.IsDeviating) return status;

        var cutoff = Now.AddMinutes(-2);
        var previous = await db.SafetyIncidents.AsNoTracking()
            .Where(x => x.RideId == rideId && x.UserId == customerId && x.Kind == IncidentKind &&
                x.RouteVersion == ride.RouteVersion && x.CreatedAtUtc >= cutoff)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(token);
        if (previous is not null)
            return status with { Accepted = true, IncidentId = previous.Id, Duplicate = true,
                Message = "تم استلام بلاغ انحراف المسار مسبقاً وإشعار الإدارة والسائق. تستمر الرحلة." };

        var location = await LatestLocationAsync(ride, token);
        var distanceText = Math.Round(status.DistanceMeters!.Value).ToString("0", CultureInfo.InvariantCulture);
        var incident = new SafetyIncident
        {
            RideId = rideId, UserId = customerId, Kind = IncidentKind, RouteVersion = ride.RouteVersion,
            Message = $"أبلغ الراكب عن انحراف السائق عن المسار المعتمد بمسافة {distanceText} متر باتجاه {status.Direction}. تم التحقق من موقع السائق على الخادم؛ تستمر الرحلة أثناء مراجعة البلاغ.",
            Latitude = location!.Latitude, Longitude = location.Longitude,
            LocationObservedAtUtc = location.ObservedAtUtc, LocationAccuracyMeters = location.Accuracy,
            LocationSource = "driverGps", DeviationMeters = status.DistanceMeters,
            DeviationDirection = status.Direction, DeviationDirectionDegrees = status.DirectionDegrees,
            VerifiedRoutePolyline = ride.RoutePolyline, CreatedAtUtc = Now
        };
        db.SafetyIncidents.Add(incident);
        await db.SaveChangesAsync(token);
        var admins = await db.Users.AsNoTracking().Where(x => x.IsActive && x.Role == UserRole.Admin)
            .Select(x => x.Id).ToListAsync(token);
        var notifications = new List<Notification>();
        foreach (var admin in admins)
            notifications.Add(new Notification { UserId = admin, Type = NotificationType.Safety,
                Title = "بلاغ انحراف عن مسار الرحلة", Body = $"الرحلة #{rideId}: انحراف بمسافة {distanceText} متر باتجاه {status.Direction}. راجع بلاغ السلامة.",
                DataJson = JsonSerializer.Serialize(new { action = "open_safety_incident", rideId, safetyIncidentId = incident.Id }) });
        notifications.Add(new Notification { UserId = ride.DriverId!.Value, Type = NotificationType.Safety,
            Title = "بلاغ انحراف عن مسار الرحلة",
            Body = $"أرسل الراكب بلاغاً بانحراف المسار {distanceText} متر باتجاه {status.Direction}. أُبلغت الإدارة وتستمر الرحلة.",
            DataJson = JsonSerializer.Serialize(new { action = "route_deviation_reported", rideId, safetyIncidentId = incident.Id }) });
        db.Notifications.AddRange(notifications);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtime is not null)
        {
            try
            {
                await realtime.PublishNotificationsCreatedAsync(notifications.Select(x =>
                    new NotificationRealtimeEvent(x.UserId, x.Id, x.Type, x.CreatedAtUtc)).ToArray(), token);
                await realtime.PublishSafetyIncidentCreatedAsync(new(incident.Id, rideId, incident.Status,
                    incident.Latitude, incident.Longitude, incident.LocationObservedAtUtc, "driverGps",
                    incident.CreatedAtUtc, incident.CreatedAtUtc), token);
            }
            catch (Exception error)
            {
                // Both the incident and the notification/push rows are durable.
                // A transient fan-out error must not make the rider submit again.
                logger?.LogWarning(error, "Route deviation incident {IncidentId} committed; realtime delivery will recover.", incident.Id);
            }
        }
        return status with { Accepted = true, IncidentId = incident.Id,
            Message = "تم قبول بلاغ انحراف المسار وإرساله إلى الإدارة وإشعار السائق. تستمر الرحلة." };
    }

    private async Task<Ride> LoadAsync(int rideId, int customerId, CancellationToken token)
    {
        var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rideId, token) ?? throw Missing();
        RequireOwner(ride, customerId);
        return ride;
    }

    private static ServiceException Missing() => new("ride_not_found", "الرحلة غير موجودة.");
    private static void RequireOwner(Ride ride, int customerId)
    {
        if (ride.CustomerId != customerId)
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية إرسال بلاغ لهذه الرحلة.");
    }

    private async Task<Ride> EnsureBaselineAsync(Ride ride, CancellationToken token)
    {
        if (!NeedsBaseline(ride)) return ride;
        var gate = BaselineLocks[(uint)ride.Id % (uint)BaselineLocks.Length];
        await gate.WaitAsync(token);
        try
        {
            ride = await db.Rides.AsNoTracking().SingleAsync(x => x.Id == ride.Id, token);
            if (!NeedsBaseline(ride)) return ride;
            // Include database identity because isolated hosts/tests can have
            // equal ride IDs. Cache is bounded and failures retry at most once/minute.
            var failureKey = $"{db.Database.GetDbConnection().Database}:{ride.Id}";
            if (BaselineFailures.TryGetValue(failureKey, out _)) return ride;
            // Compatibility with journeys booked before route polylines were saved.
            // This is fixed pickup-to-destination, never driver-to-destination.
            string polyline;
            try { polyline = (await maps.ComputeRouteAsync(ride.PickupLatitude, ride.PickupLongitude,
                ride.DestinationLatitude, ride.DestinationLongitude, false, token)).EncodedPolyline; }
            catch (ServiceException) { RememberFailure(); return ride; }
            if (RouteDeviationGeometry.Measure(polyline, ride.PickupLatitude, ride.PickupLongitude) is null)
            { RememberFailure(); return ride; }
            await db.Rides.Where(x => x.Id == ride.Id && x.RowVersion == ride.RowVersion &&
                    x.RouteVersion == 0 && (x.RoutePolyline == null || x.RoutePolyline == "") &&
                    x.Status == RideStatus.InProgress && x.ServiceEndedAtUtc == null)
                .ExecuteUpdateAsync(updates => updates.SetProperty(x => x.RoutePolyline, polyline), token);
            return await db.Rides.AsNoTracking().SingleAsync(x => x.Id == ride.Id, token);
            void RememberFailure() => BaselineFailures.Set(failureKey, true,
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
        }
        finally { gate.Release(); }
        static bool NeedsBaseline(Ride value) => value.Status == RideStatus.InProgress && value.ServiceEndedAtUtc is null &&
            string.IsNullOrWhiteSpace(value.RoutePolyline) && value.RouteVersion == 0;
    }

    private Task<LocationUpdate?> LatestLocationAsync(Ride ride, CancellationToken token) =>
        db.LocationUpdates.AsNoTracking().Where(x => x.RideId == ride.Id && x.ActorId == ride.DriverId)
            .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);

    private async Task<RouteDeviationStatus> EvaluateAsync(Ride ride, CancellationToken token)
    {
        RouteDeviationStatus Unavailable(string reason, string message, LocationUpdate? point = null) =>
            new(ride.Id, false, false, null, null, null, point?.ObservedAtUtc, ride.RouteVersion,
                reason, message, point?.Accuracy);
        if (ride.Status != RideStatus.InProgress || ride.ServiceEndedAtUtc is not null || ride.DriverId is null)
            return Unavailable("ride_not_in_progress", "التحقق من انحراف المسار متاح أثناء الرحلة الجارية فقط.");
        var location = await LatestLocationAsync(ride, token);
        if (location is null) return Unavailable("location_unavailable", "لا يتوفر موقع حديث للسائق للتحقق من انحراف المسار حالياً.");
        if (location.Source is not (null or "gps"))
            return Unavailable("location_not_gps", "الموقع الحالي تجريبي؛ يلزم موقع GPS فعلي للتحقق من البلاغ.", location);
        if (location.ObservedAtUtc < Now.AddSeconds(-30) || location.ObservedAtUtc > Now.AddSeconds(5) ||
            location.ObservedAtUtc < (ride.RouteSegmentStartedAtUtc ?? ride.StartedAtUtc ?? ride.CreatedAtUtc))
            return Unavailable("location_stale", "موقع السائق قديم؛ انتظر تحديث الموقع للتحقق من انحراف المسار.", location);
        if (location.Accuracy is not double accuracy || !double.IsFinite(accuracy) || accuracy is <= 0 or > 20)
            return Unavailable("location_inaccurate", "دقة موقع السائق غير كافية للتحقق من انحراف 20 متراً. انتظر تحديث GPS أدق.", location);
        var measurement = RouteDeviationGeometry.Measure(ride.RoutePolyline, location.Latitude, location.Longitude);
        if (measurement is null)
            return Unavailable("route_unavailable", "لا يتوفر المسار المعتمد للتحقق من البلاغ حالياً. يمكنك استخدام بلاغ السلامة العام عند الحاجة.", location);
        var deviating = RouteDeviationGeometry.IsDeviating(measurement.DistanceMeters);
        return new(ride.Id, true, deviating, measurement.DistanceMeters, measurement.Direction,
            measurement.DirectionDegrees, location.ObservedAtUtc, ride.RouteVersion,
            deviating ? "route_deviation" : "on_route", deviating
                ? $"يوجد انحراف عن مسار الرحلة بمسافة {measurement.DistanceMeters:0} متر باتجاه {measurement.Direction}. هل المسار آمن؟"
                : "لا يمكن إرسال بلاغ انحراف؛ الرحلة تسير ضمن مسارها ولا يوجد انحراف يبلغ 20 متراً يستدعي هذا البلاغ.", accuracy);
    }
}
