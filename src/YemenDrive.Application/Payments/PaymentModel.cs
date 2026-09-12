using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Payments;

public sealed record PaymentModel(
    int? Id = null,
    int? RideId = null,
    int? UserId = null,
    decimal Amount = 0,
    string Currency = "YER",
    string Provider = "Cash",
    PaymentStatus? Status = null,
    string? ProviderReference = null,
    string? IdempotencyKey = null);
