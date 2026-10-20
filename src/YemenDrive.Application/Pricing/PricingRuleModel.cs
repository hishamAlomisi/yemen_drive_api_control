namespace YemenDrive.Application.Pricing;

public sealed record PricingRuleModel(
    int? Id = null,
    int? ServiceKindId = null,
    int? ServiceCatalogItemId = null,
    decimal BaseFare = 0,
    decimal PerKilometer = 0,
    decimal PerMinute = 0,
    decimal ServiceFee = 0,
    decimal DriverCommissionRate = 0,
    decimal DriverCommissionFixed = 0,
    decimal CancellationFee = 0,
    decimal DriverShareRate = 0,
    bool IsActive = true,
    int WaitingFreeMinutes = 0,
    decimal WaitingPricePerMinute = 0,
    decimal WaitingMaxFee = 0,
    decimal MaxPriceDecreasePercent = 0,
    decimal MaxPriceIncreasePercent = 0,
    decimal RouteChangeFee = 0);
