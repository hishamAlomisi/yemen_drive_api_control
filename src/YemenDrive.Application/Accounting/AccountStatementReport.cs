using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Accounting;

public sealed class AccountStatementReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<AccountStatementModel>(config)
{
    protected override async Task<object?> ReportAsync(AccountStatementModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        var (from, until) = GetDateRange(model);
        var currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim().ToUpperInvariant();
        if (model.LedgerAccountId is int id && !await db.LedgerAccounts.AnyAsync(x => x.Id == id, token))
            throw new ServiceException("ledger_account_not_found", "الحساب المالي غير موجود.");

        var baseQuery = db.JournalLines.AsNoTracking()
            .Where(x => x.JournalEntry.IsPosted && x.JournalEntry.Status == JournalEntryStatus.Posted);
        if (model.LedgerAccountId is int accountId) baseQuery = baseQuery.Where(x => x.LedgerAccountId == accountId);
        if (currency is not null) baseQuery = baseQuery.Where(x => x.Currency == currency);

        var beforeQuery = baseQuery;
        if (from is not null) beforeQuery = beforeQuery.Where(x => x.JournalEntry.PostedAtUtc < from.Value);
        else beforeQuery = beforeQuery.Where(x => false);

        var openingRows = await beforeQuery
            .GroupBy(x => new { x.LedgerAccountId, x.LedgerAccount.Type, x.Currency })
            .Select(x => new { x.Key.LedgerAccountId, x.Key.Type, x.Key.Currency, Debit = x.Sum(y => y.Debit), Credit = x.Sum(y => y.Credit) })
            .ToListAsync(token);
        var opening = openingRows.ToDictionary(
            x => (x.LedgerAccountId, x.Currency),
            x => NaturalBalance(x.Type, x.Debit, x.Credit));

        var linesQuery = baseQuery;
        if (from is not null) linesQuery = linesQuery.Where(x => x.JournalEntry.PostedAtUtc >= from.Value);
        if (until is not null) linesQuery = linesQuery.Where(x => x.JournalEntry.PostedAtUtc < until.Value);
        if (model.EntryType is not null) linesQuery = linesQuery.Where(x => x.JournalEntry.Type == model.EntryType);

        var rawLines = await linesQuery
            .OrderBy(x => x.LedgerAccountId).ThenBy(x => x.JournalEntry.PostedAtUtc)
            .ThenBy(x => x.JournalEntry.EntryNumber).ThenBy(x => x.LineNumber)
            .Select(x => new
            {
                x.LedgerAccountId, accountCode = x.LedgerAccount.Code, accountName = x.LedgerAccount.Name,
                accountType = x.LedgerAccount.Type, x.Currency, x.Debit, x.Credit, x.Description,
                x.JournalEntryId, entryNumber = x.JournalEntry.EntryNumber,
                entryReference = x.JournalEntry.Reference, entryType = x.JournalEntry.Type,
                occurredAtUtc = x.JournalEntry.OccurredAtUtc, postedAtUtc = x.JournalEntry.PostedAtUtc,
                x.LineNumber, x.RideId, x.UserId
            })
            .ToListAsync(token);

        var balanceByAccount = new Dictionary<(int, string), decimal>(opening);
        var resultLines = rawLines.Select(x =>
        {
            var key = (x.LedgerAccountId, x.Currency);
            var current = balanceByAccount.TryGetValue(key, out var value) ? value : 0m;
            current += NaturalBalance(x.accountType, x.Debit, x.Credit);
            balanceByAccount[key] = current;
            return new
            {
                x.LedgerAccountId, x.accountCode, x.accountName, x.accountType, x.Currency,
                x.Debit, x.Credit, runningBalance = current, x.Description,
                x.JournalEntryId, x.entryNumber, x.entryReference, x.entryType,
                x.occurredAtUtc, x.postedAtUtc, x.LineNumber, x.RideId, x.UserId
            };
        }).ToArray();

        var summaries = rawLines.GroupBy(x => new { x.LedgerAccountId, x.accountCode, x.accountName, x.accountType, x.Currency })
            .Select(group =>
            {
                var key = (group.Key.LedgerAccountId, group.Key.Currency);
                var openingBalance = opening.TryGetValue(key, out var value) ? value : 0m;
                var debit = group.Sum(x => x.Debit);
                var credit = group.Sum(x => x.Credit);
                return new
                {
                    group.Key.LedgerAccountId, group.Key.accountCode, group.Key.accountName,
                    group.Key.accountType, group.Key.Currency, openingBalance,
                    debit, credit,
                    closingBalance = openingBalance + NaturalBalance(group.Key.accountType, debit, credit)
                };
            }).OrderBy(x => x.accountCode).ToArray();

        return new
        {
            dateFromUtc = from,
            dateToUtcExclusive = until,
            model.LedgerAccountId,
            model.EntryType,
            currency,
            accounts = summaries,
            lines = resultLines
        };
    }

    private static decimal NaturalBalance(LedgerAccountType type, decimal debit, decimal credit) =>
        type is LedgerAccountType.Asset or LedgerAccountType.Expense ? debit - credit : credit - debit;

    private static (DateTime? From, DateTime? Until) GetDateRange(AccountStatementModel model)
    {
        if (model.Month is not null || model.Year is not null)
        {
            var year = model.Year ?? DateTime.UtcNow.Year;
            if (year is < 2000 or > 9999) throw new ServiceException("invalid_date_range", "السنة المحددة غير صالحة.");
            if (model.Month is int month)
            {
                if (month is < 1 or > 12) throw new ServiceException("invalid_date_range", "الشهر المحدد غير صالح.");
                var from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
                return (from, from.AddMonths(1));
            }
            return (new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        var fromDate = model.DateFromUtc?.Date;
        var toDate = model.DateToUtc?.Date;
        if (fromDate is not null && toDate is not null && toDate < fromDate)
            throw new ServiceException("invalid_date_range", "تاريخ النهاية يجب أن يكون بعد أو مساوياً لتاريخ البداية.");
        return (fromDate, toDate?.AddDays(1));
    }
}
