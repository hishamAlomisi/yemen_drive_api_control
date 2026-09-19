using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

/// <summary>Administrative query model for cancellation cases awaiting a decision.</summary>
public sealed record RideCancellationReportModel(
    RideCancellationStatus? Status = null,
    int? RideId = null);
