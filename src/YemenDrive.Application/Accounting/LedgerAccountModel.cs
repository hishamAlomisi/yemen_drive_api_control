using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Accounting;

public sealed record LedgerAccountModel(
    int? Id = null,
    string? Code = null,
    string? Name = null,
    LedgerAccountType? Type = null,
    LedgerAccountPurpose Purpose = LedgerAccountPurpose.General,
    string Currency = "YER",
    bool IsActive = true,
    bool IsSystem = false,
    bool IsPosting = true,
    int? ParentLedgerAccountId = null,
    int? FinancialPartyId = null);
