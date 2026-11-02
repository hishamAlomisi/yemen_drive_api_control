using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

/// <summary>Command and read model for a cancellation case. Reason is mandatory for every customer request.</summary>
public sealed record RideCancellationRequestModel(
    int? Id = null,
    int? RideId = null,
    string? Reason = null,
    CashCancellationRefundMethod? RequestedRefundMethod = null,
    string? Note = null,
    bool PreviewOnly = false,
    decimal? ReviewedTravelledFare = null,
    int? RefundChoiceRevision = null,
    bool CashReturned = false,
    bool SafeStopConfirmed = false,
    string? Responsibility = null,
    bool? WaiveCancellationFees = null,
    bool CashReceiptConfirmed = false,
    string? EvidenceReference = null, int? FundingLedgerAccountId = null, decimal? VerifiedPayoutAmount = null,
    bool PendingOnly = false);
