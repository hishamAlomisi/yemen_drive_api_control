using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Drivers;

public sealed class DriverReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : ReportsService<DriverModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(DriverModel model, CancellationToken cancellationToken) =>
        await dbContext.DriverProfiles.AsNoTracking().Include(x => x.ServiceKind).Include(x => x.ServiceCatalogItem)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(200)
            .Select(x => new
            {
                x.Id, x.UserId, x.User.DisplayName, x.User.PhoneNumber, x.ServiceKindId, x.ServiceCatalogItemId,
                serviceKindCode = x.ServiceKind.Code, serviceKindNameAr = x.ServiceKind.NameAr,
                serviceCode = x.ServiceCatalogItem.Code, serviceNameAr = x.ServiceCatalogItem.NameAr,
                x.VehicleModel, x.PlateNumber,
                x.IsAvailable, x.Rating, x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);
}
