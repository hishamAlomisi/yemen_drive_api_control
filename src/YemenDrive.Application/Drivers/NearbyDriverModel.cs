namespace YemenDrive.Application.Drivers;

/// <summary>
/// A customer-facing query for drivers that are online, available and not
/// already serving another ride. Coordinates are used only for proximity and
/// are never persisted by this report.
/// </summary>
public sealed record NearbyDriverModel(
    double PickupLatitude,
    double PickupLongitude,
    int? ServiceKindId = null,
    int? ServiceCatalogItemId = null,
    int RadiusMeters = 10000);
