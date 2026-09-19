using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Payments;

public sealed record PaymentMethodModel(
    int? Id = null, string? Code = null, string? NameAr = null,
    string? DescriptionAr = null, string? ImageUrl = null,
    PaymentMethodKind Kind = PaymentMethodKind.ExternalWallet,
    string? ProviderCode = null, string? PublicInstructionsAr = null,
    bool IsActive = true, bool IsAvailableForRidePayment = true,
    bool IsAvailableForWalletTopUp = true, int SortOrder = 0);
