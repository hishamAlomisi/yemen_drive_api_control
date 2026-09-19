namespace YemenDrive.Application.Maps;

public sealed record MapLocationSearchModel(
    string? Query = null,
    double? Latitude = null,
    double? Longitude = null);
