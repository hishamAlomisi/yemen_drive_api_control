using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record RideWaitingReportModel(
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null,
    int? RideId = null, string? Search = null, string? Status = null,
    bool ChargedOnly = false, int Page = 1, int PageSize = 25);

public sealed class RideWaitingReport(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ReportsService<RideWaitingReportModel>(config)
{
    protected override Task<object?> ListAsync(RideWaitingReportModel model, CancellationToken ct) => ReportAsync(model, ct);
    protected override Task<object?> SearchAsync(RideWaitingReportModel model, CancellationToken ct) => ReportAsync(model, ct);

    protected override async Task<object?> ReportAsync(RideWaitingReportModel model, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new ServiceException("authentication_required", "سجّل الدخول لعرض التقرير.");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.IsActive && x.Role == UserRole.Admin, ct))
            throw new ServiceException("admin_required", "تقرير التأخيرات متاح للإدارة فقط.");
        if (model.Page < 1 || model.Page > 1_000_000 || model.PageSize is < 1 or > 100 || model.RideId <= 0 ||
            model.FromUtc >= model.ToUtc || model.Search?.Length > 100)
            throw new ServiceException("invalid_report_filter", "تحقق من الفترة ورقم الرحلة وحجم الصفحة.");

        // Filter by request time, including requests that never became a stop.
        // This report is read-only: opening it must never settle a waiting fee.
        var query = db.RideWaitingSessions.AsNoTracking().AsQueryable();
        if (model.FromUtc is { } from) query = query.Where(x => x.CreatedAtUtc >= from.UtcDateTime);
        if (model.ToUtc is { } to) query = query.Where(x => x.CreatedAtUtc < to.UtcDateTime);
        if (model.RideId is { } rideId) query = query.Where(x => x.RideId == rideId);
        if (!string.IsNullOrWhiteSpace(model.Search))
        {
            var search = model.Search.Trim();
            query = query.Where(x => (x.Ride.Customer.DisplayName != null && x.Ride.Customer.DisplayName.Contains(search)) || x.Ride.Customer.PhoneNumber.Contains(search) ||
                (x.Ride.Driver != null && ((x.Ride.Driver.DisplayName != null && x.Ride.Driver.DisplayName.Contains(search)) || x.Ride.Driver.PhoneNumber.Contains(search))));
        }
        query = model.Status switch
        {
            null or "" => query,
            "pending" => query.Where(x => x.IsOpen && x.StartedAtUtc == null),
            "active" => query.Where(x => x.IsOpen && x.StartedAtUtc != null),
            "ended" => query.Where(x => !x.IsOpen && x.StartedAtUtc != null),
            "unstarted" => query.Where(x => !x.IsOpen && x.StartedAtUtc == null),
            _ => throw new ServiceException("invalid_report_status", "حالة الانتظار غير صحيحة.")
        };
        if (model.ChargedOnly) query = query.Where(x => !x.IsOpen && x.Fee > 0);

        var summary = await query.GroupBy(x => 1).Select(g => new
        {
            sessionCount = g.Count(),
            rideCount = g.Select(x => x.RideId).Distinct().Count(),
            activeCount = g.Count(x => x.IsOpen && x.StartedAtUtc != null),
            pendingCount = g.Count(x => x.IsOpen && x.StartedAtUtc == null),
            chargedFee = g.Sum(x => x.IsOpen ? 0 : x.Fee),
            endedSeconds = g.Sum(x => !x.IsOpen && x.StartedAtUtc != null && x.EndedAtUtc != null
                ? (long)EF.Functions.DateDiffSecond(x.StartedAtUtc, x.EndedAtUtc)!.Value : 0L)
        }).SingleOrDefaultAsync(ct);
        var totalCount = summary?.sessionCount ?? 0;
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)model.PageSize));
        var page = Math.Min(model.Page, pageCount);
        var sessions = await query.Include(x => x.Ride).ThenInclude(x => x.Customer)
            .Include(x => x.Ride).ThenInclude(x => x.Driver)
            .Include(x => x.Ride).ThenInclude(x => x.ServiceCatalogItem)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * model.PageSize).Take(model.PageSize).ToListAsync(ct);
        var driverIds = sessions.Where(x => x.IsOpen && x.Ride.DriverId != null).Select(x => x.Ride.DriverId!.Value).Distinct().ToArray();
        var locations = await db.DriverLiveLocations.AsNoTracking().Where(x => driverIds.Contains(x.DriverId))
            .ToDictionaryAsync(x => x.DriverId, x => x.ObservedAtUtc, ct);
        var now = DateTime.UtcNow;
        var rows = sessions.Select(x =>
        {
            var effectiveEnd = x.EndedAtUtc ?? now;
            if (x.IsOpen && x.StartedAtUtc is { } start)
            {
                var observed = x.Ride.DriverId is { } driver && locations.TryGetValue(driver, out var time) ? time : start;
                var deadline = observed.AddSeconds(RideWaitingCoordinator.ConnectionGraceSeconds);
                if (effectiveEnd > deadline) effectiveEnd = deadline;
                if (x.PricePerMinute > 0)
                {
                    var capAt = start.AddMinutes(x.FreeMinutes).AddSeconds((double)(x.MaxFee * 60 / x.PricePerMinute));
                    if (effectiveEnd > capAt) effectiveEnd = capAt;
                }
            }
            var seconds = x.StartedAtUtc is { } began ? Math.Max(0, (effectiveEnd - began).TotalSeconds) : 0;
            return new
            {
                x.Id, x.RideId, customerName = x.Ride.Customer.DisplayName, driverName = x.Ride.Driver?.DisplayName,
                serviceName = x.Ride.ServiceCatalogItem.NameAr, rideStatus = x.Ride.Status,
                pickup = RideLocationText.DisplayName(x.Ride.PickupLabel, x.Ride.PickupAddress, "نقطة الانطلاق"),
                destination = RideLocationText.DisplayName(x.Ride.DestinationLabel, x.Ride.DestinationAddress, "الوجهة"),
                x.Latitude, x.Longitude, x.CreatedAtUtc, x.StartedAtUtc, x.EndedAtUtc, x.EndReason,
                status = x.IsOpen ? x.StartedAtUtc == null ? "pending" : "active" : x.StartedAtUtc == null ? "unstarted" : "ended",
                durationSeconds = (long)Math.Floor(seconds), freeSeconds = (long)Math.Floor(Math.Min(seconds, x.FreeMinutes * 60)),
                chargeableSeconds = (long)Math.Floor(Math.Max(0, seconds - x.FreeMinutes * 60)),
                x.FreeMinutes, x.PricePerMinute, x.MaxFee,
                chargedFee = x.IsOpen ? 0 : x.Fee,
                estimatedFee = x.IsOpen && x.StartedAtUtc != null ? RideWaitingCoordinator.FeeAt(x, effectiveEnd) : (decimal?)null,
                x.Ride.TotalAmount
            };
        }).ToList();
        return new { rows, page, pageSize = model.PageSize, pageCount, totalCount, serverNowUtc = now, currency = "YER",
            summary = (object?)summary ?? new { sessionCount = 0, rideCount = 0, activeCount = 0, pendingCount = 0, chargedFee = 0m, endedSeconds = 0L } };
    }
}
