using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Drivers;

public sealed class DriverLocation(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : OperationsService<DriverLocationModel>(configurationStore)
{
    protected override async Task<object?> AddAsync(DriverLocationModel model, CancellationToken cancellationToken) =>
        await SaveLocationAsync(model, cancellationToken);

    protected override async Task<object?> UpdateAsync(DriverLocationModel model, CancellationToken cancellationToken) =>
        await SaveLocationAsync(model, cancellationToken);

    protected override async Task<object?> GetAsync(DriverLocationModel model, CancellationToken cancellationToken)
    {
        var location = await dbContext.DriverLiveLocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DriverId == model.DriverId, cancellationToken)
            ?? throw new ServiceException("location_not_found", "موقع السائق غير موجود.");
        return ToResult(location);
    }

    private async Task<object> SaveLocationAsync(DriverLocationModel model, CancellationToken cancellationToken)
    {
        var currentUserId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول لتحديث موقع السائق.");
        var isAdmin = await dbContext.Users.AsNoTracking().AnyAsync(
            x => x.Id == currentUserId && x.Role == UserRole.Admin && x.IsActive, cancellationToken);
        if (!isAdmin && currentUserId != model.DriverId)
            throw new ServiceException("driver_location_access_denied", "لا يمكن تحديث موقع سائق آخر.");
        if (!await dbContext.DriverProfiles.AnyAsync(x => x.UserId == model.DriverId, cancellationToken))
            throw new ServiceException("driver_not_found", "السائق غير موجود.");

        var location = await dbContext.DriverLiveLocations
            .SingleOrDefaultAsync(x => x.DriverId == model.DriverId, cancellationToken);
        if (location is null)
        {
            location = new DriverLiveLocation { DriverId = model.DriverId };
            dbContext.DriverLiveLocations.Add(location);
        }

        location.Latitude = model.Latitude;
        location.Longitude = model.Longitude;
        location.Bearing = model.Bearing;
        location.Speed = model.Speed;
        location.IsOnline = model.IsOnline;
        location.ObservedAtUtc = DateTime.UtcNow;
        location.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(location);
    }

    private static object ToResult(DriverLiveLocation x) => new
    {
        x.Id, x.DriverId, x.Latitude, x.Longitude, x.Bearing, x.Speed, x.IsOnline, x.ObservedAtUtc
    };
}
