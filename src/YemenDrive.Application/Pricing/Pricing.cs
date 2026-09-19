using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Pricing;

public sealed class Pricing(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : ReportsService<PricingModel>(configurationStore)
{
    protected override async Task<object?> ReportAsync(PricingModel model, CancellationToken cancellationToken)
    {
        if (model.DistanceKm < 0 || model.DurationMinutes < 0)
            throw new ServiceException("invalid_distance", "المسافة والمدة لا يمكن أن تكونا سالبتين.");
        if (model.ServiceKindId <= 0 || model.ServiceCatalogItemId <= 0)
            throw new ServiceException("invalid_service_selection", "نوع الخدمة والخدمة المختارة مطلوبان.");

        var rule = await dbContext.PricingRules.AsNoTracking()
            .Where(x => x.IsActive && x.ServiceKindId == model.ServiceKindId && x.ServiceCatalogItemId == model.ServiceCatalogItemId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ServiceException("pricing_not_found", "لا توجد قاعدة تسعير مطابقة.");

        // The currently supported currency is the Yemeni rial. Keep the
        // quote, the accepted fare and every later financial posting in whole
        // rials so the displayed amount can never create a hidden fraction.
        var amount = RoundYemeniRial(
            rule.BaseFare + rule.PerKilometer * (decimal)model.DistanceKm +
            rule.PerMinute * (decimal)model.DurationMinutes);
        var serviceFee = RoundYemeniRial(rule.ServiceFee);
        var driverShare = RoundYemeniRial(amount * rule.DriverShareRate);
        return new
        {
            amount,
            serviceFee,
            total = amount + serviceFee,
            driverShare,
            platformShare = amount - driverShare,
            currency = "YER"
        };
    }

    private static decimal RoundYemeniRial(decimal amount) =>
        decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
}
