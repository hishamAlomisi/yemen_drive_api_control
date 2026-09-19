using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

public sealed record RideModel(
    int? Id = null,
    int? CustomerId = null,
    int? DriverId = null,
    RideStatus? Status = null,
    int? ServiceKindId = null,
    int? ServiceCatalogItemId = null,
    string? PickupLabel = null,
    string? PickupAddress = null,
    double PickupLatitude = 0,
    double PickupLongitude = 0,
    string? DestinationLabel = null,
    string? DestinationAddress = null,
    double DestinationLatitude = 0,
    double DestinationLongitude = 0,
    decimal? CustomerPrice = null,
    string? IdempotencyKey = null,
    CashCancellationRefundMethod? CashCancellationRefundMethod = null);
