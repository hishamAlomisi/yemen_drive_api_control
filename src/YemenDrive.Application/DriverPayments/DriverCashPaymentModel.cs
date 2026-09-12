namespace YemenDrive.Application.DriverPayments;

public sealed record DriverCashPaymentModel(
    int? RideId = null,
    decimal CashReceived = 0,
    string Currency = "YER",
    string? Note = null,
    string? IdempotencyKey = null);
