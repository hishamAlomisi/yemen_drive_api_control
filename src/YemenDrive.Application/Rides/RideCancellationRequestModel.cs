using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

/// <summary>Command and read model for a cancellation case. Reason is mandatory for every customer request.</summary>
public sealed record RideCancellationRequestModel(
    int? Id = null,
    int? RideId = null,
    string? Reason = null,
    CashCancellationRefundMethod? RequestedRefundMethod = null,
    string? Note = null);
