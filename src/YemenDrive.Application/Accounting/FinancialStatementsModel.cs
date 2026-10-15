namespace YemenDrive.Application.Accounting;

public sealed record FinancialStatementsModel(
    string ReportType = "position",
    DateTime? DateFromUtc = null,
    DateTime? DateToUtc = null,
    string? Currency = null);
