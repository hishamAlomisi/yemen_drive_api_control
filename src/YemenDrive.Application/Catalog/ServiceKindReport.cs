using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Catalog;

public sealed class ServiceKindReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : ReportsService<ServiceKindModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(ServiceKindModel model, CancellationToken cancellationToken) =>
        await dbContext.ServiceKinds.AsNoTracking()
            .OrderBy(x => x.SortOrder).ThenBy(x => x.NameAr)
            .Select(x => new { x.Id, x.Code, x.NameAr, x.ImageUrl, x.IsActive, x.SortOrder })
            .ToListAsync(cancellationToken);
}
