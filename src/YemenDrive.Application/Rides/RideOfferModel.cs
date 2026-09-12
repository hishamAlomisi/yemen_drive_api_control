using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

public sealed record RideOfferModel(
    int? Id = null,
    int? RideId = null,
    int? DriverId = null,
    decimal Amount = 0,
    int ValidForSeconds = 20,
    string? Note = null,
    OfferStatus? Status = null);
