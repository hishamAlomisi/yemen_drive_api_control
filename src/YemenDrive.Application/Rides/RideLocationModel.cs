using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record RideLocationModel(
    int? RideId = null,
    double Latitude = 0,
    double Longitude = 0,
    double? Bearing = null,
    double? Speed = null);

public sealed class RideLocation(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<RideLocationModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["add", "create"];

    protected override async Task<object?> AddAsync(RideLocationModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول لتحديث موقع الرحلة.");
        if (model.RideId is null || model.Latitude is < -90 or > 90 || model.Longitude is < -180 or > 180)
            throw new ServiceException("invalid_location", "معرف الرحلة وإحداثيات الموقع الصحيحة مطلوبة.");

        var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.RideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != userId && ride.DriverId != userId)
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية تحديث موقع هذه الرحلة.");
        if (ride.Status is RideStatus.Completed or RideStatus.Cancelled)
            throw new ServiceException("ride_not_trackable", "لا يمكن تحديث موقع رحلة منتهية أو ملغاة.");

        var entity = new LocationUpdate
        {
            RideId = ride.Id,
            ActorId = userId,
            Latitude = model.Latitude,
            Longitude = model.Longitude,
            Bearing = model.Bearing,
            Speed = model.Speed,
            ObservedAtUtc = DateTime.UtcNow
        };
        db.LocationUpdates.Add(entity);
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    private static object ToResult(LocationUpdate x) => new
    {
        x.Id, x.RideId, x.ActorId, x.Latitude, x.Longitude, x.Bearing, x.Speed, x.ObservedAtUtc
    };
}

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
