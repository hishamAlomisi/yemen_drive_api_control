using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Catalog;

public sealed class ServiceCatalogReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : ReportsService<ServiceCatalogModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(ServiceCatalogModel model, CancellationToken cancellationToken)
    {
        var serviceKindId = model.ServiceKindId;
        return await dbContext.ServiceCatalogItems.AsNoTracking()
            .Where(x => serviceKindId == null || x.ServiceKindId == serviceKindId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.NameAr)
            .Select(x => new
            {
                x.Id, x.Code, x.NameAr, x.ServiceKindId,
                serviceKindCode = x.ServiceKind.Code, serviceKindNameAr = x.ServiceKind.NameAr,
                x.ArrivalMinutes,
                x.BasePrice, x.Rating, x.Seats, x.DescriptionAr, x.ImageUrl, x.IsRecommended, x.IsActive, x.SortOrder
            })
            .ToListAsync(cancellationToken);
    }
}
