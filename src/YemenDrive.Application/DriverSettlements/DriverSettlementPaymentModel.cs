namespace YemenDrive.Application.DriverSettlements;

public sealed record DriverSettlementPaymentModel(
    int? Id = null,
    int? DriverSettlementId = null,
    decimal Amount = 0,
    string Currency = "YER",
    string Method = "CashToPlatform",
    string? Reference = null,
    string? Note = null);
