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
    public override IReadOnlyCollection<string> Operations { get; } = ["get"];

    protected override async Task<object?> GetAsync(DriverLocationModel model, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول لعرض موقع السائق.");
        var isAdmin = await dbContext.Users.AsNoTracking().AnyAsync(
            x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive,
            cancellationToken);
        if (!isAdmin && userId != model.DriverId)
            throw new ServiceException(
                "driver_location_access_denied", "لا تملك صلاحية عرض موقع هذا السائق.");

        var location = await dbContext.DriverLiveLocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DriverId == model.DriverId, cancellationToken)
            ?? throw new ServiceException("location_not_found", "موقع السائق غير موجود.");
        return ToResult(location);
    }

    private static object ToResult(DriverLiveLocation x) => new
    {
        x.Id, x.DriverId, x.Latitude, x.Longitude, x.Bearing, x.Speed, x.IsOnline, x.ObservedAtUtc
    };
}
