using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record RideRouteChangeReportModel(DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null,
    int? RideId = null, string? Status = null, int Page = 1, int PageSize = 25, string? Search = null);

public sealed class RideRouteChangeReport(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ReportsService<RideRouteChangeReportModel>(config)
{
    protected override Task<object?> ListAsync(RideRouteChangeReportModel model, CancellationToken ct) => ReportAsync(model, ct);
    protected override Task<object?> SearchAsync(RideRouteChangeReportModel model, CancellationToken ct) => ReportAsync(model, ct);
    protected override async Task<object?> ReportAsync(RideRouteChangeReportModel model, CancellationToken ct)
    {
        if (user.UserId is not { } id || !await db.Users.AsNoTracking().AnyAsync(x => x.Id == id && x.IsActive && x.Role == UserRole.Admin, ct))
            throw new ServiceException("admin_required", "سجل تغييرات المسار متاح للإدارة فقط.");
        if (model.Page is < 1 or > 1_000_000 || model.PageSize is < 1 or > 100 || model.FromUtc >= model.ToUtc || model.RideId <= 0 || model.Search?.Length > 100)
            throw new ServiceException("invalid_report_filter", "تحقق من الفترة ورقم الرحلة وحجم الصفحة.");
        var query = db.RideRouteChanges.AsNoTracking().AsQueryable();
        if (model.FromUtc is { } from) query = query.Where(x => x.CreatedAtUtc >= from.UtcDateTime);
        if (model.ToUtc is { } to) query = query.Where(x => x.CreatedAtUtc < to.UtcDateTime);
        if (model.RideId is { } rideId) query = query.Where(x => x.RideId == rideId);
        if (!string.IsNullOrWhiteSpace(model.Search))
        {
            var search = model.Search.Trim();
            query = query.Where(x => (x.Ride.Customer.DisplayName != null && x.Ride.Customer.DisplayName.Contains(search)) || x.Ride.Customer.PhoneNumber.Contains(search) ||
                (x.Ride.Driver != null && ((x.Ride.Driver.DisplayName != null && x.Ride.Driver.DisplayName.Contains(search)) || x.Ride.Driver.PhoneNumber.Contains(search))));
        }
        if (!string.IsNullOrEmpty(model.Status))
        {
            if (model.Status is not ("quoted" or "pending" or "accepted" or "rejected" or "cancelled" or "expired"))
                throw new ServiceException("invalid_report_status", "حالة طلب تغيير المسار غير صحيحة.");
            query = query.Where(x => x.Status == model.Status);
        }
        var summary = await query.GroupBy(x => 1).Select(g => new { requestCount = g.Count(),
            acceptedCount = g.Count(x => x.Status == "accepted"), chargedFee = g.Sum(x => x.Status == "accepted" ? x.RouteChangeFee : 0) }).SingleOrDefaultAsync(ct);
        var totalCount = summary?.requestCount ?? 0;
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalCount / (double)model.PageSize));
        var page = Math.Min(model.Page, pageCount);
        var rows = await query.Include(x => x.Ride).ThenInclude(x => x.Customer).Include(x => x.Ride).ThenInclude(x => x.Driver)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Skip((page - 1) * model.PageSize).Take(model.PageSize).ToListAsync(ct);
        return new { page, pageSize = model.PageSize, pageCount, totalCount, serverNowUtc = DateTime.UtcNow,
            summary = (object?)summary ?? new { requestCount = 0, acceptedCount = 0, chargedFee = 0m },
            rows = rows.Select(x => new { x.Id, x.RideId, customerName = x.Ride.Customer.DisplayName, driverName = x.Ride.Driver?.DisplayName,
                x.OldDestinationLabel, x.NewDestinationLabel, x.ChangeLatitude, x.ChangeLongitude, x.Status, x.CreatedAtUtc,
                x.ExpiresAtUtc, x.DecidedAtUtc, invoice = RideRouteChangeService.Invoice(x) }) };
    }
}
