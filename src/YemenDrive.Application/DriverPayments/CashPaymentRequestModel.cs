namespace YemenDrive.Application.DriverPayments;

/// <summary>Customer request and driver decision for a post-completion cash collection.</summary>
public sealed record CashPaymentRequestModel(
    int? Id = null,
    int? RideId = null,
    string? Note = null,
    string? IdempotencyKey = null);
