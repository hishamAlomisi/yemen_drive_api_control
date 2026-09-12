using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Pricing;

public sealed class PricingRuleReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : ReportsService<PricingRuleModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(PricingRuleModel model, CancellationToken cancellationToken) =>
        await dbContext.PricingRules.AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id, x.ServiceKindId, x.ServiceCatalogItemId,
                serviceKindCode = x.ServiceKind.Code, serviceKindNameAr = x.ServiceKind.NameAr,
                serviceCode = x.ServiceCatalogItem.Code, serviceNameAr = x.ServiceCatalogItem.NameAr, x.BaseFare,
                x.PerKilometer, x.PerMinute, x.DriverShareRate, x.IsActive
            })
            .ToListAsync(cancellationToken);
}
