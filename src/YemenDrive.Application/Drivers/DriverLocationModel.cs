namespace YemenDrive.Application.Drivers;

public sealed record DriverLocationModel(
    int DriverId,
    double Latitude,
    double Longitude,
    double? Bearing = null,
    double? Speed = null,
    bool IsOnline = true);
