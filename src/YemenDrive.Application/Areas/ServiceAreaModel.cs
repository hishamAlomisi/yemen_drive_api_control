namespace YemenDrive.Application.Areas;

public sealed record ServiceAreaModel(
    int? Id = null,
    string? CountryCode = null,
    string? CountryNameAr = null,
    string? CityNameAr = null,
    bool IsActive = true);
