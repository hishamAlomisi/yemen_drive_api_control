using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Pricing;

public static class RidePricingPolicy
{
    public static decimal Round(decimal amount) => decimal.Round(amount, 0, MidpointRounding.AwayFromZero);

    public static (decimal Min, decimal Max) Bounds(decimal amount, decimal decreasePercent, decimal increasePercent) =>
        (Math.Max(1, decimal.Ceiling(amount * (1 - decreasePercent / 100))),
         Math.Min(999999999, decimal.Floor(amount * (1 + increasePercent / 100))));

    public static void ValidateCustomerPrice(Ride ride, decimal? amount)
    {
        if (amount is null || amount <= 0 || amount > 999999999 || amount != decimal.Truncate(amount.Value))
            throw new ServiceException("invalid_customer_price", "أدخل أجرة صحيحة أكبر من صفر.");
        // Legacy searches retain their last stored price until a new server quote is obtained.
        var min = ride.MinCustomerPrice ?? ride.CustomerPrice ?? ride.ServerPrice;
        var max = ride.MaxCustomerPrice ?? ride.CustomerPrice ?? ride.ServerPrice;
        if (min is null || max is null || amount < min || amount > max)
            throw new ServiceException("customer_price_out_of_range", $"الأجرة المسموحة من {min:0} إلى {max:0} ر.ي وفق قاعدة التسعير.");
    }

    public static async Task ApplyQuoteAsync(YemenDriveDbContext db, Ride ride, Guid? token, CancellationToken ct)
    {
        var quote = token is null ? null : await db.RidePricingQuotes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Token == token, ct);
        if (quote is null || quote.ExpiresAtUtc <= DateTime.UtcNow)
            throw new ServiceException("pricing_quote_expired", "حدّث تسعيرة الرحلة ثم أعد المحاولة.");
        if (quote.CustomerId != ride.CustomerId || quote.ServiceKindId != ride.ServiceKindId ||
            quote.ServiceCatalogItemId != ride.ServiceCatalogItemId ||
            Math.Abs(quote.PickupLatitude - ride.PickupLatitude) > 0.000001 ||
            Math.Abs(quote.PickupLongitude - ride.PickupLongitude) > 0.000001 ||
            Math.Abs(quote.DestinationLatitude - ride.DestinationLatitude) > 0.000001 ||
            Math.Abs(quote.DestinationLongitude - ride.DestinationLongitude) > 0.000001)
            throw new ServiceException("pricing_quote_mismatch", "التسعيرة لا تخص هذه الرحلة. حدّث السعر بعد تغيير الموقع أو الخدمة.");
        ride.PricingRuleId = quote.PricingRuleId;
        ride.HasRoutePricingSnapshot = quote.HasRoutePricingSnapshot;
        ride.RouteBaseFare = quote.BaseFare;
        ride.RoutePerKilometer = quote.PerKilometer;
        ride.RoutePerMinute = quote.PerMinute;
        ride.RouteQuotedDistanceMeters = quote.DistanceMeters;
        ride.RouteQuotedDurationSeconds = quote.DurationSeconds;
        ride.RouteChangeFee = quote.RouteChangeFee;
        ride.QuotedPrice = quote.Amount;
        ride.MinCustomerPrice = quote.MinPrice;
        ride.MaxCustomerPrice = quote.MaxPrice;
        ride.ServiceFee = quote.ServiceFee;
        ride.CancellationFee = quote.CancellationFee;
        ride.AppliedDriverCommissionRate = quote.DriverCommissionRate;
        ride.AppliedDriverCommissionFixed = quote.DriverCommissionFixed;
        ride.WaitingFreeMinutes = quote.WaitingFreeMinutes;
        ride.WaitingPricePerMinute = quote.WaitingPricePerMinute;
        ride.WaitingMaxFee = quote.WaitingMaxFee;
        ValidateCustomerPrice(ride, ride.CustomerPrice);
    }

    public static void UpdateTotals(Ride ride)
    {
        var fare = (ride.CustomerPrice ?? ride.ServerPrice ?? 0) + ride.WaitingFee;
        ride.TotalAmount = fare + ride.ServiceFee;
        ride.DriverCommissionAmount = Round(fare * ride.AppliedDriverCommissionRate + ride.AppliedDriverCommissionFixed);
        if (ride.DriverCommissionAmount > fare)
            throw new ServiceException("invalid_driver_commission", "عمولة السائق لا يمكن أن تتجاوز أجرة الرحلة.");
        ride.DriverShare = fare - ride.DriverCommissionAmount;
        ride.PlatformShare = ride.ServiceFee + ride.DriverCommissionAmount;
    }

    public static async Task AcceptFareAsync(YemenDriveDbContext db, Ride ride, decimal amount, CancellationToken ct)
    {
        if (ride.QuotedPrice is not null) ValidateCustomerPrice(ride, amount);
        if (ride.PricingRuleId is null)
        {
            var rule = await db.PricingRules.AsNoTracking().Where(x => x.IsActive &&
                x.ServiceKindId == ride.ServiceKindId && x.ServiceCatalogItemId == ride.ServiceCatalogItemId)
                .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            ride.ServiceFee = Round(rule?.ServiceFee ?? 0);
            ride.CancellationFee = Round(rule?.CancellationFee ?? 0);
            ride.AppliedDriverCommissionRate = rule?.DriverCommissionRate ?? 0;
            ride.AppliedDriverCommissionFixed = rule?.DriverCommissionFixed ?? 0;
            // Existing rides never acquire a newly enabled waiting policy.
        }
        ride.ServerPrice = Round(amount);
        ride.CustomerPrice = Round(amount);
        ride.RouteOriginalFare = Round(amount);
        UpdateTotals(ride);
    }
}
