using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Accounting;

public sealed record AccountStatementModel(
    int? LedgerAccountId = null,
    DateTime? DateFromUtc = null,
    DateTime? DateToUtc = null,
    int? Month = null,
    int? Year = null,
    JournalEntryType? EntryType = null,
    string? Currency = null);
