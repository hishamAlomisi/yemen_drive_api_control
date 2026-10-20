using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using YemenDrive.Application.Maps;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Pricing;

public sealed class Pricing(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser,
    GoogleMapsGateway gateway) : ReportsService<PricingModel>(configurationStore)
{
    protected override async Task<object?> ReportAsync(PricingModel model, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "سجل الدخول لجلب التسعيرة.");
        if (model.CustomerId is int customerId && customerId != userId)
        {
            if (!await dbContext.Users.AnyAsync(x => x.Id == userId && x.IsActive && x.Role == UserRole.Admin, cancellationToken))
                throw new ServiceException("admin_required", "لا يمكن طلب تسعيرة باسم عميل آخر.");
            userId = customerId;
        }
        if (!double.IsFinite(model.DistanceKm) || !double.IsFinite(model.DurationMinutes) || model.DistanceKm < 0 || model.DurationMinutes < 0)
            throw new ServiceException("invalid_distance", "المسافة والمدة لا يمكن أن تكونا سالبتين.");
        if (model.ServiceKindId <= 0 || model.ServiceCatalogItemId <= 0)
            throw new ServiceException("invalid_service_selection", "نوع الخدمة والخدمة المختارة مطلوبان.");

        var rule = await dbContext.PricingRules.AsNoTracking()
            .Where(x => x.IsActive && x.ServiceKindId == model.ServiceKindId && x.ServiceCatalogItemId == model.ServiceCatalogItemId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new ServiceException("pricing_not_found", "لا توجد قاعدة تسعير مطابقة.");

        var distance = model.DistanceKm;
        var duration = model.DurationMinutes;
        var hasRoute = model.PickupLatitude is not null && model.PickupLongitude is not null &&
            model.DestinationLatitude is not null && model.DestinationLongitude is not null;
        if (hasRoute)
        {
            ValidateCoordinates(model.PickupLatitude!.Value, model.PickupLongitude!.Value);
            ValidateCoordinates(model.DestinationLatitude!.Value, model.DestinationLongitude!.Value);
            // Passenger-supplied distance, duration and bounds never set a contractual price.
            var route = await gateway.ComputeRouteAsync(model.PickupLatitude.Value, model.PickupLongitude.Value,
                model.DestinationLatitude.Value, model.DestinationLongitude.Value, false, cancellationToken);
            distance = route.DistanceMeters / 1000d;
            duration = route.DurationSeconds / 60d;
        }
        else if (!await dbContext.Users.AnyAsync(x => x.Id == userId && x.IsActive && x.Role == UserRole.Admin, cancellationToken))
            throw new ServiceException("route_required", "حدّد نقطتي الانطلاق والوصول لجلب التسعيرة.");

        // The currently supported currency is the Yemeni rial. Keep the
        // quote, the accepted fare and every later financial posting in whole
        // rials so the displayed amount can never create a hidden fraction.
        var amount = RoundYemeniRial(
            rule.BaseFare + rule.PerKilometer * (decimal)distance +
            rule.PerMinute * (decimal)duration);
        if (amount <= 0 || amount > 999999999)
            throw new ServiceException("invalid_fare", "لا توجد أجرة صالحة لهذه الرحلة. راجع قاعدة التسعير.");
        var (minPrice, maxPrice) = RidePricingPolicy.Bounds(amount, rule.MaxPriceDecreasePercent, rule.MaxPriceIncreasePercent);
        var serviceFee = RoundYemeniRial(rule.ServiceFee);
        RidePricingQuote? quote = null;
        if (hasRoute)
        {
            quote = new RidePricingQuote
            {
                HasRoutePricingSnapshot = true, BaseFare = rule.BaseFare, PerKilometer = rule.PerKilometer,
                PerMinute = rule.PerMinute, DistanceMeters = distance * 1000, DurationSeconds = duration * 60,
                RouteChangeFee = rule.RouteChangeFee,
                CustomerId = userId, ServiceKindId = rule.ServiceKindId, ServiceCatalogItemId = rule.ServiceCatalogItemId,
                PricingRuleId = rule.Id, Amount = amount, MinPrice = minPrice, MaxPrice = maxPrice,
                PickupLatitude = model.PickupLatitude!.Value, PickupLongitude = model.PickupLongitude!.Value,
                DestinationLatitude = model.DestinationLatitude!.Value, DestinationLongitude = model.DestinationLongitude!.Value,
                ServiceFee = serviceFee, CancellationFee = RidePricingPolicy.Round(rule.CancellationFee),
                DriverCommissionRate = rule.DriverCommissionRate, DriverCommissionFixed = rule.DriverCommissionFixed,
                WaitingFreeMinutes = rule.WaitingFreeMinutes, WaitingPricePerMinute = rule.WaitingPricePerMinute,
                WaitingMaxFee = rule.WaitingMaxFee, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
            };
            dbContext.RidePricingQuotes.Add(quote);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        var driverShare = RoundYemeniRial(amount * rule.DriverShareRate);
        return new
        {
            amount,
            rule.RouteChangeFee,
            minPrice, maxPrice, rule.MaxPriceDecreasePercent, rule.MaxPriceIncreasePercent,
            rule.WaitingFreeMinutes, rule.WaitingPricePerMinute, rule.WaitingMaxFee,
            pricingQuoteToken = quote?.Token, expiresAtUtc = quote?.ExpiresAtUtc,
            serviceFee,
            total = amount + serviceFee,
            driverShare,
            platformShare = amount - driverShare,
            currency = "YER"
        };
    }

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ServiceException("invalid_map_coordinates", "إحداثيات الرحلة غير صالحة.");
    }

    private static decimal RoundYemeniRial(decimal amount) =>
        decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
}
