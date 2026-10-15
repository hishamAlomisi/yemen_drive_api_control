namespace YemenDrive.Application.Accounting;

public sealed record FinancialOperationModel(
    int JournalEntryId = 0,
    string OperationType = "",
    int DebitAccountId = 0,
    int CreditAccountId = 0,
    decimal Amount = 0,
    DateOnly OperationDate = default,
    string Note = "");
