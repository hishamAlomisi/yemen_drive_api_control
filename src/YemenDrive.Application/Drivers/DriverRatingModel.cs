namespace YemenDrive.Application.Drivers;

public sealed record DriverRatingModel(
    int? Id = null,
    int? DriverUserId = null,
    int? RideId = null,
    decimal? Score = null,
    string? Comment = null);
