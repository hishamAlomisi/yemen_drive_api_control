namespace YemenDrive.Application.Pricing;

public sealed record PricingModel(
    int ServiceKindId,
    int ServiceCatalogItemId,
    double DistanceKm,
    double DurationMinutes,
    double? PickupLatitude = null,
    double? PickupLongitude = null,
    double? DestinationLatitude = null,
    double? DestinationLongitude = null,
    int? CustomerId = null);
