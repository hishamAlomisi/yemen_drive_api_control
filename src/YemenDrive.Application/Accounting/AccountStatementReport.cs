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
        List<LedgerAccountSnapshot> statementAccounts;
        int[]? selectedAccountIds = null;
        if (model.LedgerAccountId is int id)
        {
            var selectedAccount = await db.LedgerAccounts.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new LedgerAccountSnapshot(
                    x.Id, x.Code, x.Name, x.Type, x.Currency, x.ParentLedgerAccountId, x.IsPosting))
                .SingleOrDefaultAsync(token);
            if (selectedAccount is null)
                throw new ServiceException("ledger_account_not_found", "الحساب المالي غير موجود.");

            var treeIds = new HashSet<int> { id };
            statementAccounts = new List<LedgerAccountSnapshot> { selectedAccount };
            var frontier = new[] { id };
            while (frontier.Length > 0)
            {
                var children = await db.LedgerAccounts.AsNoTracking()
                    .Where(x => x.ParentLedgerAccountId.HasValue && frontier.Contains(x.ParentLedgerAccountId.Value))
                    .Select(x => new LedgerAccountSnapshot(
                        x.Id, x.Code, x.Name, x.Type, x.Currency, x.ParentLedgerAccountId, x.IsPosting))
                    .ToListAsync(token);
                var nextFrontier = children.Where(x => treeIds.Add(x.Id)).ToArray();
                statementAccounts.AddRange(nextFrontier);
                frontier = nextFrontier.Select(x => x.Id).ToArray();
            }
            selectedAccountIds = treeIds.ToArray();
        }
        else
        {
            statementAccounts = await db.LedgerAccounts.AsNoTracking()
                .Select(x => new LedgerAccountSnapshot(
                    x.Id, x.Code, x.Name, x.Type, x.Currency, x.ParentLedgerAccountId, x.IsPosting))
                .ToListAsync(token);
        }

        var baseQuery = db.JournalLines.AsNoTracking()
            .Where(x => x.JournalEntry.IsPosted && x.JournalEntry.Status == JournalEntryStatus.Posted);
        if (selectedAccountIds is not null)
            baseQuery = baseQuery.Where(x => selectedAccountIds.Contains(x.LedgerAccountId));
        if (currency is not null) baseQuery = baseQuery.Where(x => x.Currency == currency);

        var beforeQuery = baseQuery;
        if (from is not null) beforeQuery = beforeQuery.Where(x => x.JournalEntry.OccurredAtUtc < from.Value);
        else beforeQuery = beforeQuery.Where(x => false);

        var openingRows = await beforeQuery
            .GroupBy(x => new { x.LedgerAccountId, x.LedgerAccount.Type, x.Currency })
            .Select(x => new { x.Key.LedgerAccountId, x.Key.Type, x.Key.Currency, Debit = x.Sum(y => y.Debit), Credit = x.Sum(y => y.Credit) })
            .ToListAsync(token);
        var opening = openingRows.ToDictionary(
            x => (x.LedgerAccountId, x.Currency),
            x => NaturalBalance(x.Type, x.Debit, x.Credit));

        var linesQuery = baseQuery;
        if (from is not null) linesQuery = linesQuery.Where(x => x.JournalEntry.OccurredAtUtc >= from.Value);
        if (until is not null) linesQuery = linesQuery.Where(x => x.JournalEntry.OccurredAtUtc < until.Value);
        if (model.EntryType is not null) linesQuery = linesQuery.Where(x => x.JournalEntry.Type == model.EntryType);

        var rawLines = await linesQuery
            .OrderBy(x => x.LedgerAccountId).ThenBy(x => x.JournalEntry.OccurredAtUtc)
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

        var accountById = statementAccounts.ToDictionary(x => x.Id);
        var directSummaries = rawLines.GroupBy(x => new { x.LedgerAccountId, x.accountCode, x.accountName, x.accountType, x.Currency })
            .Select(group =>
            {
                var key = (group.Key.LedgerAccountId, group.Key.Currency);
                var openingBalance = opening.TryGetValue(key, out var value) ? value : 0m;
                var debit = group.Sum(x => x.Debit);
                var credit = group.Sum(x => x.Credit);
                var account = accountById[group.Key.LedgerAccountId];
                return new StatementAccountSummary(
                    group.Key.LedgerAccountId, group.Key.accountCode, group.Key.accountName,
                    group.Key.accountType, group.Key.Currency, openingBalance, debit, credit,
                    openingBalance + NaturalBalance(group.Key.accountType, debit, credit),
                    HasChildren: false, account.ParentLedgerAccountId, account.IsPosting);
            }).ToList();

        var currenciesByAccount = new Dictionary<int, HashSet<string>>();
        void AddAccountCurrency(int accountId, string accountCurrency)
        {
            if (!currenciesByAccount.TryGetValue(accountId, out var currencies))
                currenciesByAccount[accountId] = currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            currencies.Add(accountCurrency);
        }

        foreach (var row in openingRows) AddAccountCurrency(row.LedgerAccountId, row.Currency);
        foreach (var row in rawLines) AddAccountCurrency(row.LedgerAccountId, row.Currency);

        var directSummaryKeys = directSummaries.Select(x => (x.LedgerAccountId, x.Currency)).ToHashSet();
        foreach (var account in statementAccounts)
        {
            var currencies = currenciesByAccount.TryGetValue(account.Id, out var accountCurrencies)
                ? accountCurrencies.ToArray()
                : Array.Empty<string>();
            if (currencies.Length == 0)
                currencies = [currency ?? account.Currency];

            foreach (var accountCurrency in currencies)
            {
                if (!directSummaryKeys.Add((account.Id, accountCurrency))) continue;
                var openingBalance = opening.TryGetValue((account.Id, accountCurrency), out var priorBalance)
                    ? priorBalance
                    : 0m;
                directSummaries.Add(new StatementAccountSummary(
                    account.Id, account.Code, account.Name, account.Type, accountCurrency,
                    openingBalance, 0m, 0m, openingBalance, HasChildren: false,
                    account.ParentLedgerAccountId, account.IsPosting));
            }
        }

        var parentIds = statementAccounts.Where(x => x.ParentLedgerAccountId.HasValue)
            .Select(x => x.ParentLedgerAccountId!.Value).ToHashSet();
        var rollups = new Dictionary<(int AccountId, string Currency), StatementAccountSummary>();
        foreach (var direct in directSummaries)
        {
            var currentId = direct.LedgerAccountId;
            while (accountById.TryGetValue(currentId, out var current))
            {
                if (parentIds.Contains(current.Id))
                {
                    var key = (current.Id, direct.Currency);
                    if (rollups.TryGetValue(key, out var existing))
                        rollups[key] = existing with
                        {
                            OpeningBalance = existing.OpeningBalance + direct.OpeningBalance,
                            Debit = existing.Debit + direct.Debit,
                            Credit = existing.Credit + direct.Credit,
                            ClosingBalance = existing.ClosingBalance + direct.ClosingBalance
                        };
                    else
                        rollups[key] = new StatementAccountSummary(
                            current.Id, current.Code, current.Name, current.Type, direct.Currency,
                            direct.OpeningBalance, direct.Debit, direct.Credit, direct.ClosingBalance,
                            HasChildren: true, current.ParentLedgerAccountId, current.IsPosting);
                }

                if (current.ParentLedgerAccountId is not int parentId) break;
                currentId = parentId;
            }
        }

        var summaries = new List<StatementAccountSummary>();
        var directSummariesByAccount = directSummaries.GroupBy(x => x.LedgerAccountId)
            .ToDictionary(x => x.Key, x => x.ToArray());
        var rollupsByAccount = rollups.Values.GroupBy(x => x.LedgerAccountId)
            .ToDictionary(x => x.Key, x => x.ToArray());
        foreach (var account in statementAccounts)
        {
            if (parentIds.Contains(account.Id))
            {
                if (rollupsByAccount.TryGetValue(account.Id, out var parentRollups))
                    summaries.AddRange(parentRollups);
                else
                    summaries.Add(new StatementAccountSummary(
                        account.Id, account.Code, account.Name, account.Type, currency ?? account.Currency,
                        0m, 0m, 0m, 0m, HasChildren: true,
                        account.ParentLedgerAccountId, account.IsPosting));
            }
            else
            {
                if (directSummariesByAccount.TryGetValue(account.Id, out var accountSummaries))
                    summaries.AddRange(accountSummaries);
            }
        }
        var orderedSummaries = summaries.OrderBy(x => x.AccountCode).ThenBy(x => x.Currency).ToArray();

        return new
        {
            dateFromUtc = from,
            dateToUtcExclusive = until,
            model.LedgerAccountId,
            model.EntryType,
            currency,
            accounts = orderedSummaries,
            lines = resultLines
        };
    }

    private static decimal NaturalBalance(LedgerAccountType type, decimal debit, decimal credit) =>
        type is LedgerAccountType.Asset or LedgerAccountType.Expense ? debit - credit : credit - debit;

    private sealed record LedgerAccountSnapshot(
        int Id,
        string Code,
        string Name,
        LedgerAccountType Type,
        string Currency,
        int? ParentLedgerAccountId,
        bool IsPosting);

    private sealed record StatementAccountSummary(
        int LedgerAccountId,
        string AccountCode,
        string AccountName,
        LedgerAccountType AccountType,
        string Currency,
        decimal OpeningBalance,
        decimal Debit,
        decimal Credit,
        decimal ClosingBalance,
        bool HasChildren,
        int? ParentLedgerAccountId,
        bool IsPosting);

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
