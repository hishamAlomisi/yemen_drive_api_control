using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record RideLocationModel(
    int? RideId = null);

public sealed class RideLocationReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<RideLocationModel>(config)
{
    protected override async Task<object?> ListAsync(RideLocationModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول لعرض مسار الرحلة.");
        if (model.RideId is null)
            throw new ServiceException("ride_required", "معرف الرحلة مطلوب.");
        var participant = await db.Rides.AsNoTracking().AnyAsync(
            x => x.Id == model.RideId && (x.CustomerId == userId || x.DriverId == userId), token);
        if (!participant)
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية عرض مسار هذه الرحلة.");

        return await db.LocationUpdates.AsNoTracking()
            .Where(x => x.RideId == model.RideId)
            .OrderByDescending(x => x.ObservedAtUtc).Take(500)
            .OrderBy(x => x.ObservedAtUtc)
            .Select(x => new { x.Id, x.RideId, x.ActorId, x.Latitude, x.Longitude, x.Bearing, x.Speed, x.ObservedAtUtc })
            .ToListAsync(token);
    }
}
