namespace YemenDrive.Application.Pricing;

public sealed record PricingModel(
    int ServiceKindId,
    int ServiceCatalogItemId,
    double DistanceKm,
    double DurationMinutes);
