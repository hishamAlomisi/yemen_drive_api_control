using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record DriverMovementReportModel(int? DriverId = null, DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null, int Page = 1, int PageSize = 25, string? Search = null);

public sealed class DriverMovementReport(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ReportsService<DriverMovementReportModel>(config)
{
    protected override Task<object?> ListAsync(DriverMovementReportModel model, CancellationToken ct) => ReportAsync(model, ct);

    protected override async Task<object?> SearchAsync(DriverMovementReportModel model, CancellationToken ct)
    {
        await AdministratorAccess.RequireAsync(db, user.RequireUserId(), "operations.read", ct);
        Validate(model);
        var query = db.Users.AsNoTracking().Where(x => x.Role == UserRole.Driver || db.Rides.Any(r => r.DriverId == x.Id));
        if (!string.IsNullOrWhiteSpace(model.Search))
        {
            var term = model.Search.Trim();
            query = query.Where(x => (x.DisplayName != null && x.DisplayName.Contains(term)) || x.PhoneNumber.Contains(term));
        }
        return await query.OrderBy(x => x.DisplayName).ThenBy(x => x.Id).Take(1000)
            .Select(x => new { x.Id, x.DisplayName, x.PhoneNumber }).ToListAsync(ct);
    }

    protected override async Task<object?> ReportAsync(DriverMovementReportModel model, CancellationToken ct)
    {
        await AdministratorAccess.RequireAsync(db, user.RequireUserId(), "operations.read", ct);
        Validate(model);
        var query = db.Rides.AsNoTracking().Where(x => x.DriverId != null);
        if (model.DriverId is { } driverId) query = query.Where(x => x.DriverId == driverId);
        if (model.FromUtc is { } from) query = query.Where(x => x.CreatedAtUtc >= from.UtcDateTime);
        if (model.ToUtc is { } to) query = query.Where(x => x.CreatedAtUtc < to.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(model.Search))
        {
            var term = model.Search.Trim();
            query = query.Where(x => (x.Customer.DisplayName != null && x.Customer.DisplayName.Contains(term)) ||
                x.Customer.PhoneNumber.Contains(term) || x.Driver!.DisplayName != null && x.Driver.DisplayName.Contains(term));
        }
        var summary = await query.GroupBy(x => 1).Select(g => new
        {
            rideCount = g.Count(), completedCount = g.Count(x => x.Status == RideStatus.Completed),
            cancelledCount = g.Count(x => x.Status == RideStatus.Cancelled),
            activeCount = g.Count(x => x.ServiceEndedAtUtc == null && x.Status != RideStatus.Completed && x.Status != RideStatus.Cancelled)
        }).SingleOrDefaultAsync(ct);
        var totalCount = summary?.rideCount ?? 0;
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)model.PageSize));
        var page = Math.Min(model.Page, pageCount);
        var rides = await query.Include(x => x.Customer).Include(x => x.Driver).Include(x => x.Offers)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * model.PageSize).Take(model.PageSize).ToListAsync(ct);
        var ids = rides.Select(x => x.Id).ToArray();
        var snapshots = await db.RideMovementSnapshots.AsNoTracking().Where(x => ids.Contains(x.RideId)).ToDictionaryAsync(x => x.RideId, ct);
        var ratings = (await db.DriverRatings.AsNoTracking().Where(x => x.RideId != null && ids.Contains(x.RideId.Value) && x.Source == DriverRatingSource.Customer)
            .OrderByDescending(x => x.Id).ToListAsync(ct)).GroupBy(x => x.RideId!.Value).ToDictionary(x => x.Key, x => x.First());
        var changes = await db.RideRouteChanges.AsNoTracking().Where(x => ids.Contains(x.RideId) && x.Status == "accepted")
            .GroupBy(x => x.RideId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var stops = (await db.RideWaitingSessions.AsNoTracking().Where(x => ids.Contains(x.RideId) && x.StartedAtUtc != null)
            .ToListAsync(ct)).ToLookup(x => x.RideId);
        var support = await db.SupportTickets.AsNoTracking().Where(x => x.RideId != null && ids.Contains(x.RideId.Value))
            .GroupBy(x => x.RideId!.Value).Select(g => new { Id = g.Key, Count = g.Count(), Open = g.Count(x => x.Status != "Closed" && x.Status != "Resolved") }).ToDictionaryAsync(x => x.Id, ct);
        var safety = await db.SafetyIncidents.AsNoTracking().Where(x => x.RideId != null && ids.Contains(x.RideId.Value))
            .GroupBy(x => x.RideId!.Value).Select(g => new { Id = g.Key, Count = g.Count(), Open = g.Count(x => x.Status != SafetyIncidentStatus.Resolved) }).ToDictionaryAsync(x => x.Id, ct);
        var now = DateTime.UtcNow;
        var rows = rides.Select(ride =>
        {
            snapshots.TryGetValue(ride.Id, out var snapshot);
            ratings.TryGetValue(ride.Id, out var rating);
            support.TryGetValue(ride.Id, out var tickets);
            safety.TryGetValue(ride.Id, out var incidents);
            var accepted = MovementTimes.Accepted(ride, snapshot);
            var end = MovementTimes.End(ride, now);
            var waits = stops[ride.Id].ToArray();
            var duration = MovementTimes.Seconds(ride.StartedAtUtc, end);
            var estimatedDuration = snapshot?.PlannedDurationSeconds ??
                (ride.RouteVersion == 0 && ride.RouteQuotedDurationSeconds > 0 ? ride.RouteQuotedDurationSeconds : (double?)null);
            var delayAssessed = ride.Status == RideStatus.Completed && ride.RouteVersion == 0 &&
                changes.GetValueOrDefault(ride.Id) == 0 && duration != null && estimatedDuration > 0;
            return new
            {
                id = ride.Id, rideId = ride.Id, ride.DriverId, driverName = ride.Driver?.DisplayName,
                customerName = ride.Customer.DisplayName, ride.Status, createdAtUtc = MovementTimes.Utc(ride.CreatedAtUtc),
                acceptedAtUtc = MovementTimes.Utc(accepted), driverArrivedAtUtc = MovementTimes.Utc(ride.DriverArrivedAtUtc),
                startedAtUtc = MovementTimes.Utc(ride.StartedAtUtc), serviceEndedAtUtc = MovementTimes.Utc(ride.ServiceEndedAtUtc ?? ride.CompletedAtUtc),
                ratingScore = rating?.Score, ratingComment = rating?.Comment,
                arrivalDurationSeconds = MovementTimes.Seconds(accepted, ride.DriverArrivedAtUtc),
                arrivalElapsedSeconds = ride.DriverArrivedAtUtc == null && ride.StartedAtUtc == null ? MovementTimes.Seconds(accepted, end) : null,
                tripDurationSeconds = duration,
                plannedTripDurationSeconds = estimatedDuration,
                tripDelayAssessed = delayAssessed,
                tripDelaySeconds = delayAssessed ? (long?)Math.Max(0, Math.Floor(duration!.Value - estimatedDuration!.Value)) : null,
                arrivalDelayAssessed = false,
                arrivalDelayNote = "لا يوجد تقدير وصول محفوظ لتقييم تأخر الوصول.",
                routeChanged = ride.RouteVersion > 0 || changes.GetValueOrDefault(ride.Id) > 0,
                routeChangeCount = changes.GetValueOrDefault(ride.Id),
                delayCount = waits.Length,
                delayDurationSeconds = waits.Sum(x => MovementTimes.Seconds(x.StartedAtUtc, x.EndedAtUtc) ?? 0),
                activeDelayCount = waits.Count(x => x.IsOpen),
                delayBasis = "recorded_waiting_sessions",
                complaintCount = (tickets?.Count ?? 0) + (incidents?.Count ?? 0),
                openComplaintCount = (tickets?.Open ?? 0) + (incidents?.Open ?? 0),
                supportComplaintCount = tickets?.Count ?? 0, safetyComplaintCount = incidents?.Count ?? 0,
                pickup = RideLocationText.DisplayName(ride.PickupLabel, ride.PickupAddress, "نقطة الانطلاق"),
                destination = RideLocationText.DisplayName(ride.DestinationLabel, ride.DestinationAddress, "الوجهة")
            };
        }).ToArray();
        return new { rows, page, pageSize = model.PageSize, pageCount, totalCount, serverNowUtc = now,
            summary = (object?)summary ?? new { rideCount = 0, completedCount = 0, cancelledCount = 0, activeCount = 0 } };
    }

    private static void Validate(DriverMovementReportModel model)
    {
        if (model.Page is < 1 or > 1_000_000 || model.PageSize is < 1 or > 100 || model.DriverId <= 0 ||
            model.FromUtc >= model.ToUtc || model.Search?.Length > 100)
            throw new ServiceException("invalid_report_filter", "تحقق من السائق والفترة وحجم الصفحة.");
    }
}

internal static class MovementTimes
{
    internal static DateTime? Accepted(YemenDrive.Database.Entities.Ride ride, RideMovementSnapshot? snapshot) =>
        snapshot?.AcceptedAtUtc ?? ride.Offers.Where(x => x.DriverId == ride.DriverId && x.Status == OfferStatus.Accepted)
            .OrderBy(x => x.UpdatedAtUtc).Select(x => x.UpdatedAtUtc).FirstOrDefault();
    internal static DateTime? End(YemenDrive.Database.Entities.Ride ride, DateTime now) =>
        ride.ServiceEndedAtUtc ?? ride.CompletedAtUtc ?? (ride.Status is RideStatus.Completed or RideStatus.Cancelled ? (DateTime?)null : now);
    internal static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    internal static DateTime? Utc(DateTime? value) => value is { } time ? Utc(time) : null;
    internal static long? Seconds(DateTime? from, DateTime? to) => from is { } start && to is { } end && end >= start
        ? (long)Math.Floor((end - start).TotalSeconds) : null;
}
