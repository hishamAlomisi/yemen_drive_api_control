namespace YemenDrive.Application.Drivers;

public sealed record DriverModel(
    int? Id = null,
    int? UserId = null,
    int? ServiceKindId = null,
    int? ServiceCatalogItemId = null,
    string? VehicleModel = null,
    string? PlateNumber = null,
    decimal CommissionRate = 0,
    bool IsAvailable = true,
    decimal Rating = 0,
    string? PhotoUrl = null);
