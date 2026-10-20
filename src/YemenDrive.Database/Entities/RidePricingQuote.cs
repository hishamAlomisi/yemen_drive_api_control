namespace YemenDrive.Database.Entities;

/// <summary>A short-lived, server-calculated offer bound to its customer and route.</summary>
public sealed class RidePricingQuote : Entity
{
    public bool HasRoutePricingSnapshot { get; set; }
    public decimal BaseFare { get; set; }
    public decimal PerKilometer { get; set; }
    public decimal PerMinute { get; set; }
    public double DistanceMeters { get; set; }
    public double DurationSeconds { get; set; }
    public decimal RouteChangeFee { get; set; }
    public Guid Token { get; set; } = Guid.NewGuid();
    public int CustomerId { get; set; }
    public int ServiceKindId { get; set; }
    public int ServiceCatalogItemId { get; set; }
    public int PricingRuleId { get; set; }
    public double PickupLatitude { get; set; }
    public double PickupLongitude { get; set; }
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public decimal Amount { get; set; }
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public decimal ServiceFee { get; set; }
    public decimal CancellationFee { get; set; }
    public decimal DriverCommissionRate { get; set; }
    public decimal DriverCommissionFixed { get; set; }
    public int WaitingFreeMinutes { get; set; }
    public decimal WaitingPricePerMinute { get; set; }
    public decimal WaitingMaxFee { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
