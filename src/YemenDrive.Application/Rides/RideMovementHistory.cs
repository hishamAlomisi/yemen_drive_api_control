using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record RideMovementHistoryModel(int? RideId = null);
public sealed record MovementMapPoint(double Latitude, double Longitude, DateTime ObservedAtUtc, double? Accuracy, string Source);

public sealed class RideMovementHistory(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : OperationsService<RideMovementHistoryModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get"];

    protected override async Task<object?> GetAsync(RideMovementHistoryModel model, CancellationToken ct)
    {
        await AdministratorAccess.RequireAsync(db, user.RequireUserId(), "operations.read", ct);
        if (model.RideId is not > 0) throw new ServiceException("ride_required", "اختر الرحلة لعرض مساراتها.");
        var ride = await db.Rides.AsNoTracking().Include(x => x.Offers).SingleOrDefaultAsync(x => x.Id == model.RideId, ct)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        var snapshot = await db.RideMovementSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.RideId == ride.Id, ct);
        var origin = await db.RideRequestDriverLocations.AsNoTracking().SingleOrDefaultAsync(x => x.RideId == ride.Id && x.DriverId == ride.DriverId, ct);
        var now = DateTime.UtcNow;
        var accepted = MovementTimes.Accepted(ride, snapshot);
        var recordedEnd = MovementTimes.End(ride, now);
        var end = recordedEnd ?? now;
        var approachEnd = ride.DriverArrivedAtUtc ?? ride.StartedAtUtc ?? end;
        if (approachEnd > end) approachEnd = end;
        var approach = ride.DriverId is int driver && accepted is { } start
            ? await SamplesAsync(ride.Id, driver, start, approachEnd, ct) : [];
        var trip = ride.DriverId is int assigned && ride.StartedAtUtc is { } tripStart
            ? await SamplesAsync(ride.Id, assigned, tripStart, end, ct) : [];
        var changes = await db.RideRouteChanges.AsNoTracking().Where(x => x.RideId == ride.Id && x.Status == "accepted")
            .OrderBy(x => x.DecidedAtUtc).Select(x => new { x.Id, x.ChangeLatitude, x.ChangeLongitude,
                observedAtUtc = x.ChangeObservedAtUtc, x.OldDestinationLabel, x.NewDestinationLabel,
                x.NewDestinationLatitude, x.NewDestinationLongitude, x.NewRoutePolyline, x.DecidedAtUtc }).ToListAsync(ct);
        var notes = new List<string>();
        if (recordedEnd is null) notes.Add("وقت نهاية هذه الرحلة القديمة غير محفوظ؛ تُعرض المواقع المرتبطة بسجلها دون تقدير مدة غير معلومة.");
        if (origin is null) notes.Add("لم يُحفظ موقع هذا السائق عند إرسال الطلب؛ لا يُستبدل بموقعه الحالي.");
        if (snapshot?.PassengerObservedAtUtc is null) notes.Add("موقع GPS للراكب عند الطلب غير محفوظ؛ علامة الانطلاق هي الموقع الذي اختاره للرحلة.");
        if (approach.Count == 0) notes.Add("لا توجد عينات محفوظة لمسار وصول السائق إلى الراكب.");
        if (trip.Count == 0) notes.Add("لا توجد عينات محفوظة لمسار الرحلة.");
        var tested = ride.DriverId is int sampledDriver && await db.LocationUpdates.AsNoTracking().AnyAsync(x =>
            x.RideId == ride.Id && x.ActorId == sampledDriver && x.ObservedAtUtc >= ride.CreatedAtUtc && x.ObservedAtUtc <= end &&
            (x.Source == "admin_manual" || x.Source == "admin_simulation"), ct);
        if (tested) notes.Add("يتضمن السجل مواقع أدخلتها الإدارة للتجربة.");
        notes.Add("المسارات مبنية على المواقع المحفوظة؛ تفصل الانقطاعات الكبيرة، ولا تثبت حركة غير مسجلة.");
        return new
        {
            rideId = ride.Id, ride.DriverId, ride.Status, requestedAtUtc = MovementTimes.Utc(ride.CreatedAtUtc),
            acceptedAtUtc = MovementTimes.Utc(accepted), driverAtRequest = origin == null ? null : new
            { origin.Latitude, origin.Longitude, observedAtUtc = MovementTimes.Utc(origin.ObservedAtUtc),
                sentAtUtc = MovementTimes.Utc(origin.SentAtUtc), source = "request_invitation" },
            passengerAtRequest = snapshot?.PassengerObservedAtUtc is { } passengerTime && snapshot.PassengerLatitude is { } lat && snapshot.PassengerLongitude is { } lon
                ? new MovementMapPoint(lat, lon, MovementTimes.Utc(passengerTime), snapshot.PassengerAccuracy, "passenger_gps") : null,
            pickup = new { latitude = snapshot?.PickupLatitude ?? ride.PickupLatitude, longitude = snapshot?.PickupLongitude ?? ride.PickupLongitude,
                label = snapshot?.PickupLabel ?? RideLocationText.DisplayName(ride.PickupLabel, ride.PickupAddress, "نقطة الانطلاق") },
            destination = new { latitude = ride.DestinationLatitude, longitude = ride.DestinationLongitude,
                label = RideLocationText.DisplayName(ride.DestinationLabel, ride.DestinationAddress, "الوجهة") },
            originalDestination = snapshot == null ? null : new { latitude = snapshot.DestinationLatitude, longitude = snapshot.DestinationLongitude, label = snapshot.DestinationLabel },
            approachSamples = approach, tripSamples = trip, approachSegments = Segments(approach), tripSegments = Segments(trip),
            plannedRoutePolyline = snapshot?.PlannedRoutePolyline ?? (ride.RouteVersion == 0 ? ride.RoutePolyline : null),
            routeChanges = changes.Select(x => new { x.Id, x.ChangeLatitude, x.ChangeLongitude, observedAtUtc = MovementTimes.Utc(x.observedAtUtc),
                x.OldDestinationLabel, x.NewDestinationLabel, x.NewDestinationLatitude, x.NewDestinationLongitude, x.NewRoutePolyline, decidedAtUtc = MovementTimes.Utc(x.DecidedAtUtc) }),
            historyAvailable = approach.Count > 0 || trip.Count > 0, hasSimulatedSamples = tested, notes, serverNowUtc = now
        };
    }

    private async Task<IReadOnlyList<MovementMapPoint>> SamplesAsync(int rideId, int driverId, DateTime start, DateTime end, CancellationToken ct)
    {
        if (end < start) return [];
        var bucket = Math.Max(1, (int)Math.Ceiling((end - start).TotalSeconds / 600));
        var query = db.LocationUpdates.AsNoTracking().Where(x => x.RideId == rideId && x.ActorId == driverId &&
            x.ObservedAtUtc >= start && x.ObservedAtUtc <= end && x.Latitude >= -90 && x.Latitude <= 90 &&
            x.Longitude >= -180 && x.Longitude <= 180 && (x.Accuracy == null || x.Accuracy > 0 && x.Accuracy <= 75));
        var points = await query.GroupBy(x => new { Bucket = EF.Functions.DateDiffSecond(start, x.ObservedAtUtc) / bucket, x.Source })
            .Select(group => group.OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.Id).First()).ToListAsync(ct);
        var last = await query.OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (last != null) points.Add(last);
        return points.DistinctBy(x => x.Id).OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.Id)
            .Select(x => new MovementMapPoint(x.Latitude, x.Longitude, MovementTimes.Utc(x.ObservedAtUtc), x.Accuracy, x.Source ?? "legacy"))
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<MovementMapPoint>> Segments(IReadOnlyList<MovementMapPoint> points)
    {
        var result = new List<IReadOnlyList<MovementMapPoint>>();
        var current = new List<MovementMapPoint>();
        foreach (var point in points)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                var gap = (point.ObservedAtUtc - previous.ObservedAtUtc).TotalSeconds;
                var distance = RideWaitingCoordinator.DistanceMeters(previous.Latitude, previous.Longitude, point.Latitude, point.Longitude);
                if (gap > 120 || distance > 75 * Math.Max(0, gap) + 75 || point.Source == "admin_manual")
                { result.Add(current.ToArray()); current.Clear(); }
            }
            current.Add(point);
        }
        if (current.Count > 0) result.Add(current.ToArray());
        return result;
    }
}
