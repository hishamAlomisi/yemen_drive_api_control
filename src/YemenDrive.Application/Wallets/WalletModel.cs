using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Wallets;

public sealed record WalletModel(
    int? UserId = null,
    decimal Amount = 0,
    WalletTransactionType Type = WalletTransactionType.Credit,
    string Description = "",
    int? RideId = null,
    string? ExternalReference = null);
