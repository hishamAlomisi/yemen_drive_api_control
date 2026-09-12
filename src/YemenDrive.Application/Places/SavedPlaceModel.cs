namespace YemenDrive.Application.Places;

public sealed record SavedPlaceModel(int? Id = null, string? Label = null, string? Kind = null, string? Address = null, double? Latitude = null, double? Longitude = null);
