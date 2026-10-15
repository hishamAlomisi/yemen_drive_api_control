using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Accounting;

/// <summary>Read-only financial statements derived from posted general-ledger entries.</summary>
public sealed class FinancialStatementsReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<FinancialStatementsModel>(config)
{
    private const string Position = "position";
    private const string TrialBalance = "trialBalance";
    private const string Income = "income";
    private const string CashFlow = "cashFlow";
    private const string Equity = "equity";

    protected override async Task<object?> ReportAsync(FinancialStatementsModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        var reportType = NormalizeReportType(model.ReportType);
        var currency = NormalizeCurrency(model.Currency);
        return reportType switch
        {
            Position => await PositionAsync(model, currency, token),
            TrialBalance => await TrialBalanceAsync(model, currency, token),
            Income => await IncomeAsync(model, currency, token),
            CashFlow => await CashFlowAsync(model, currency, token),
            Equity => await EquityChangesAsync(model, currency, token),
            _ => throw new ServiceException("financial_report_not_supported", "نوع التقرير المالي غير مدعوم.")
        };
    }

    private async Task<object> PositionAsync(FinancialStatementsModel model, string? currency, CancellationToken token)
    {
        var (dateFrom, until) = ResolveAsOf(model);
        var balances = await GetMovementsAsync(null, until, currency, token);
        var rows = balances
            .Select(x => new FinancialAccountRow(x.LedgerAccountId, x.AccountCode, x.AccountName, x.AccountType,
                x.Currency, NaturalBalance(x.AccountType, x.Debit, x.Credit)))
            .Where(x => x.Amount != 0)
            .OrderBy(x => x.AccountCode, StringComparer.Ordinal)
            .ThenBy(x => x.Currency, StringComparer.Ordinal)
            .ToArray();
        var currencies = rows.Select(x => x.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

        var sections = new[]
        {
            new FinancialSection("assets", "الأصول", rows.Where(x => x.AccountType == LedgerAccountType.Asset).ToArray()),
            new FinancialSection("liabilities", "الالتزامات", rows.Where(x => x.AccountType == LedgerAccountType.Liability).ToArray()),
            new FinancialSection("equity", "حقوق الملكية", BuildPositionEquityRows(rows, currencies))
        };
        var totals = currencies.Select(code =>
        {
            var assets = Sum(rows, code, LedgerAccountType.Asset);
            var liabilities = Sum(rows, code, LedgerAccountType.Liability);
            var capital = Sum(rows, code, LedgerAccountType.Equity);
            var retainedEarnings = rows.Where(x => x.Currency == code &&
                x.AccountType is LedgerAccountType.Revenue or LedgerAccountType.Expense)
                .Sum(x => x.Amount);
            var totalEquity = capital + retainedEarnings;
            return new PositionTotal(code, assets, liabilities, capital, retainedEarnings,
                totalEquity, liabilities + totalEquity, assets - liabilities - totalEquity);
        }).ToArray();

        return new
        {
            reportType = Position,
            dateFromUtc = dateFrom,
            dateToUtc = until.AddDays(-1),
            currency,
            sections,
            totals,
            note = "تعرض القائمة أرصدة القيود المنشورة حتى التاريخ المحدد. تضاف الأرباح أو الخسائر المتراكمة غير المقفلة إلى حقوق الملكية تلقائياً."
        };
    }

    private static FinancialAccountRow[] BuildPositionEquityRows(
        FinancialAccountRow[] rows, IReadOnlyCollection<string> currencies)
    {
        var result = rows.Where(x => x.AccountType == LedgerAccountType.Equity).ToList();
        foreach (var currency in currencies)
        {
            var retainedEarnings = rows.Where(x => x.Currency == currency &&
                x.AccountType is LedgerAccountType.Revenue or LedgerAccountType.Expense)
                .Sum(x => x.Amount);
            if (retainedEarnings != 0)
                result.Add(new FinancialAccountRow(0, "RETAINED", "أرباح وخسائر متراكمة غير مقفلة",
                    LedgerAccountType.Equity, currency, retainedEarnings));
        }
        return result.OrderBy(x => x.AccountCode, StringComparer.Ordinal).ThenBy(x => x.Currency, StringComparer.Ordinal).ToArray();
    }

    private async Task<object> TrialBalanceAsync(FinancialStatementsModel model, string? currency, CancellationToken token)
    {
        var (from, until) = ResolvePeriod(model);
        var opening = await GetMovementsAsync(null, from, currency, token);
        var period = await GetMovementsAsync(from, until, currency, token);
        var keys = opening.Concat(period).Select(x => (x.LedgerAccountId, x.Currency)).Distinct().ToArray();
        var openingMap = opening.ToDictionary(x => (x.LedgerAccountId, x.Currency));
        var periodMap = period.ToDictionary(x => (x.LedgerAccountId, x.Currency));
        var metadata = opening.Concat(period).GroupBy(x => (x.LedgerAccountId, x.Currency))
            .ToDictionary(x => x.Key, x => x.First());

        var rows = keys.Select(key =>
        {
            openingMap.TryGetValue(key, out var beginning);
            periodMap.TryGetValue(key, out var activity);
            metadata.TryGetValue(key, out var account);
            var openingNet = (beginning?.Debit ?? 0m) - (beginning?.Credit ?? 0m);
            var closingNet = openingNet + (activity?.Debit ?? 0m) - (activity?.Credit ?? 0m);
            return new TrialBalanceRow(key.LedgerAccountId, account!.AccountCode, account.AccountName, key.Currency,
                Math.Max(openingNet, 0), Math.Max(-openingNet, 0),
                activity?.Debit ?? 0m, activity?.Credit ?? 0m,
                Math.Max(closingNet, 0), Math.Max(-closingNet, 0));
        }).OrderBy(x => x.AccountCode, StringComparer.Ordinal).ThenBy(x => x.Currency, StringComparer.Ordinal).ToArray();

        var totals = rows.GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase).Select(group => new TrialBalanceTotal(
            group.Key,
            group.Sum(x => x.OpeningDebit), group.Sum(x => x.OpeningCredit),
            group.Sum(x => x.Debit), group.Sum(x => x.Credit),
            group.Sum(x => x.ClosingDebit), group.Sum(x => x.ClosingCredit))).ToArray();

        return new
        {
            reportType = TrialBalance,
            dateFromUtc = from,
            dateToUtc = until.AddDays(-1),
            currency,
            rows,
            totals,
            note = "يعرض ميزان المراجعة الأرصدة المدينة والدائنة والحركة خلال الفترة من القيود المنشورة، مع فصل العملات."
        };
    }

    private async Task<object> IncomeAsync(FinancialStatementsModel model, string? currency, CancellationToken token)
    {
        var (from, until) = ResolvePeriod(model);
        var movements = await GetMovementsAsync(from, until, currency, token);
        var rows = movements
            .Where(x => x.AccountType is LedgerAccountType.Revenue or LedgerAccountType.Expense)
            .Select(x => new FinancialAccountRow(x.LedgerAccountId, x.AccountCode, x.AccountName, x.AccountType, x.Currency,
                NaturalBalance(x.AccountType, x.Debit, x.Credit)))
            .Where(x => x.Amount != 0)
            .OrderBy(x => x.AccountCode, StringComparer.Ordinal)
            .ThenBy(x => x.Currency, StringComparer.Ordinal)
            .ToArray();
        var sections = new[]
        {
            new FinancialSection("revenue", "الإيرادات", rows.Where(x => x.AccountType == LedgerAccountType.Revenue).ToArray()),
            new FinancialSection("expenses", "المصروفات", rows.Where(x => x.AccountType == LedgerAccountType.Expense).ToArray())
        };
        var totals = rows.GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var revenue = group.Where(x => x.AccountType == LedgerAccountType.Revenue).Sum(x => x.Amount);
            var expenses = group.Where(x => x.AccountType == LedgerAccountType.Expense).Sum(x => x.Amount);
            return new IncomeTotal(group.Key, revenue, expenses, revenue - expenses);
        }).ToArray();

        return new
        {
            reportType = Income,
            dateFromUtc = from,
            dateToUtc = until.AddDays(-1),
            currency,
            sections,
            totals,
            note = "تُحسب الإيرادات والمصروفات من حركة حساباتها خلال الفترة المحددة؛ صافي الربح هو الإيرادات ناقص المصروفات."
        };
    }

    private async Task<object> CashFlowAsync(FinancialStatementsModel model, string? currency, CancellationToken token)
    {
        var (from, until) = ResolvePeriod(model);
        var opening = await GetMovementsAsync(null, from, currency, token);
        var cashAccountIds = await GetCashAccountIdsAsync(token);
        var openingCash = opening.Where(x => IsCashAccount(x, cashAccountIds)).GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Debit - y.Credit), StringComparer.OrdinalIgnoreCase);
        var activity = await GetCashActivitiesAsync(from, until, currency, cashAccountIds, token);
        var currencies = openingCash.Keys.Concat(activity.Select(x => x.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var rows = activity.Select(x =>
        {
            var amount = x.Debit - x.Credit;
            var (category, label) = CashFlowClassification(x.EntryType);
            if (x.IsReversal) label = $"عكس: {label}";
            return new CashFlowActivityRow(category, label, x.Currency, x.Debit, x.Credit, amount);
        }).OrderBy(x => x.Currency, StringComparer.Ordinal).ThenBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Operation, StringComparer.Ordinal).ToArray();

        var totals = currencies.Select(code =>
        {
            var currencyRows = rows.Where(x => string.Equals(x.Currency, code, StringComparison.OrdinalIgnoreCase)).ToArray();
            var openingAmount = openingCash.TryGetValue(code, out var value) ? value : 0m;
            var operatingInflow = currencyRows.Where(x => x.Category == "operating").Sum(x => x.Inflow);
            var operatingOutflow = currencyRows.Where(x => x.Category == "operating").Sum(x => x.Outflow);
            var investing = currencyRows.Where(x => x.Category == "investing").Sum(x => x.Net);
            var financing = currencyRows.Where(x => x.Category == "financing").Sum(x => x.Net);
            var unclassified = currencyRows.Where(x => x.Category == "unclassified").Sum(x => x.Net);
            var openingAdjustments = currencyRows.Where(x => x.Category == "opening").Sum(x => x.Net);
            var netChange = operatingInflow - operatingOutflow + investing + financing + unclassified + openingAdjustments;
            return new CashFlowTotal(code, openingAmount, operatingInflow, operatingOutflow, investing, financing,
                unclassified, openingAdjustments, netChange, openingAmount + netChange);
        }).ToArray();

        return new
        {
            reportType = CashFlow,
            dateFromUtc = from,
            dateToUtc = until.AddDays(-1),
            currency,
            rows,
            totals,
            note = "تُحتسب الحركة من القيود المنشورة على حسابات الصندوق والمحافظ الخارجية. قيود الاستثمار والتمويل غير معرفة حالياً في النظام، لذلك لا تُعرض لها حركات إلا بعد إضافة هذه الأنواع المحاسبية."
        };
    }

    private async Task<object> EquityChangesAsync(FinancialStatementsModel model, string? currency, CancellationToken token)
    {
        var (from, until) = ResolvePeriod(model);
        var opening = await GetMovementsAsync(null, from, currency, token);
        var activity = await GetMovementsAsync(from, until, currency, token);
        var priorIncome = opening.Where(x => x.AccountType is LedgerAccountType.Revenue or LedgerAccountType.Expense)
            .GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(y => NaturalBalance(y.AccountType, y.Debit, y.Credit)), StringComparer.OrdinalIgnoreCase);
        var periodIncome = activity.Where(x => x.AccountType is LedgerAccountType.Revenue or LedgerAccountType.Expense)
            .GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(y => NaturalBalance(y.AccountType, y.Debit, y.Credit)), StringComparer.OrdinalIgnoreCase);
        var openingEquity = opening.Where(x => x.AccountType == LedgerAccountType.Equity).ToDictionary(x => (x.LedgerAccountId, x.Currency));
        var equityActivity = activity.Where(x => x.AccountType == LedgerAccountType.Equity).ToDictionary(x => (x.LedgerAccountId, x.Currency));
        var accountKeys = openingEquity.Keys.Concat(equityActivity.Keys).Distinct().ToArray();
        var accountMetadata = opening.Concat(activity).Where(x => x.AccountType == LedgerAccountType.Equity)
            .GroupBy(x => (x.LedgerAccountId, x.Currency)).ToDictionary(x => x.Key, x => x.First());
        var rows = accountKeys.Select(key =>
        {
            openingEquity.TryGetValue(key, out var start);
            equityActivity.TryGetValue(key, out var move);
            accountMetadata.TryGetValue(key, out var account);
            var openingBalance = start is null ? 0m : NaturalBalance(start.AccountType, start.Debit, start.Credit);
            var increase = move?.Credit ?? 0m;
            var decrease = move?.Debit ?? 0m;
            return new EquityChangeRow(key.LedgerAccountId, account!.AccountCode, account.AccountName, key.Currency,
                openingBalance, increase, decrease, 0m, openingBalance + increase - decrease);
        }).ToList();
        var currencies = opening.Select(x => x.Currency).Concat(activity.Select(x => x.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        foreach (var code in currencies)
        {
            var retained = priorIncome.TryGetValue(code, out var prior) ? prior : 0m;
            if (retained != 0) rows.Add(new EquityChangeRow(0, "RETAINED", "أرباح وخسائر متراكمة قبل الفترة", code, retained, 0, 0, 0, retained));
            var income = periodIncome.TryGetValue(code, out var current) ? current : 0m;
            if (income != 0) rows.Add(new EquityChangeRow(0, "CURRENT-RESULT", "صافي ربح أو خسارة الفترة", code, 0, 0, 0, income, income));
        }
        var orderedRows = rows.OrderBy(x => x.Currency, StringComparer.Ordinal).ThenBy(x => x.AccountCode, StringComparer.Ordinal).ToArray();
        var totals = currencies.Select(code =>
        {
            var currencyRows = orderedRows.Where(x => string.Equals(x.Currency, code, StringComparison.OrdinalIgnoreCase)).ToArray();
            var openingTotal = currencyRows.Sum(x => x.OpeningBalance);
            var increase = currencyRows.Sum(x => x.Increase);
            var decrease = currencyRows.Sum(x => x.Decrease);
            var periodNetIncome = currencyRows.Sum(x => x.PeriodNetIncome);
            return new EquityChangeTotal(code, openingTotal, increase, decrease, periodNetIncome,
                openingTotal + increase - decrease + periodNetIncome);
        }).ToArray();

        return new
        {
            reportType = Equity,
            dateFromUtc = from,
            dateToUtc = until.AddDays(-1),
            currency,
            rows = orderedRows,
            totals,
            note = "تُجمع حركات حسابات حقوق الملكية مع صافي نتيجة الفترة، وتُدرج النتائج السابقة غير المقفلة ضمن رصيد بداية الفترة."
        };
    }

    private IQueryable<JournalLine> PostedLines() => db.JournalLines.AsNoTracking()
        .Where(x => x.JournalEntry.IsPosted && x.JournalEntry.Status == JournalEntryStatus.Posted);

    private async Task<List<AccountMovement>> GetMovementsAsync(
        DateTime? from, DateTime until, string? currency, CancellationToken token)
    {
        var query = PostedLines().Where(x => x.JournalEntry.OccurredAtUtc < until);
        if (from is not null) query = query.Where(x => x.JournalEntry.OccurredAtUtc >= from.Value);
        if (currency is not null) query = query.Where(x => x.Currency == currency);
        return await query.GroupBy(x => new
            {
                x.LedgerAccountId, x.Currency, x.LedgerAccount.Code, x.LedgerAccount.Name,
                x.LedgerAccount.Type, x.LedgerAccount.Purpose
            })
            .Select(group => new AccountMovement(
                group.Key.LedgerAccountId, group.Key.Code, group.Key.Name, group.Key.Type, group.Key.Purpose,
                group.Key.Currency, group.Sum(x => x.Debit), group.Sum(x => x.Credit)))
            .ToListAsync(token);
    }

    private async Task<List<CashActivity>> GetCashActivitiesAsync(
        DateTime? from, DateTime until, string? currency, IReadOnlyCollection<int> cashAccountIds, CancellationToken token)
    {
        var query = PostedLines().Where(x => x.JournalEntry.OccurredAtUtc < until &&
            (cashAccountIds.Contains(x.LedgerAccountId) ||
             x.LedgerAccount.Purpose == LedgerAccountPurpose.ExternalWallet));
        if (from is not null) query = query.Where(x => x.JournalEntry.OccurredAtUtc >= from.Value);
        if (currency is not null) query = query.Where(x => x.Currency == currency);
        return await query.GroupBy(x => new
            {
                x.Currency,
                EntryType = x.JournalEntry.Type == JournalEntryType.Reversal && x.JournalEntry.ReversesJournalEntry != null
                    ? x.JournalEntry.ReversesJournalEntry.Type : x.JournalEntry.Type,
                IsReversal = x.JournalEntry.Type == JournalEntryType.Reversal
            })
            .Select(group => new CashActivity(group.Key.Currency, group.Key.EntryType, group.Key.IsReversal,
                group.Sum(x => x.Debit), group.Sum(x => x.Credit)))
            .ToListAsync(token);
    }

    private async Task<HashSet<int>> GetCashAccountIdsAsync(CancellationToken token)
    {
        var accounts = await db.LedgerAccounts.AsNoTracking()
            .Select(x => new { x.Id, x.Code, x.Purpose, x.ParentLedgerAccountId })
            .ToListAsync(token);
        var childrenByParent = accounts.Where(x => x.ParentLedgerAccountId.HasValue)
            .GroupBy(x => x.ParentLedgerAccountId!.Value)
            .ToDictionary(x => x.Key, x => x.Select(account => account.Id).ToArray());
        var cashAccountIds = new HashSet<int>();
        var frontier = new Queue<int>(accounts
            .Where(x => x.Code == "1000" || x.Purpose == LedgerAccountPurpose.CashCollection)
            .Select(x => x.Id));
        while (frontier.TryDequeue(out var accountId))
        {
            if (!cashAccountIds.Add(accountId)) continue;
            if (!childrenByParent.TryGetValue(accountId, out var childIds)) continue;
            foreach (var childId in childIds) frontier.Enqueue(childId);
        }
        return cashAccountIds;
    }

    private static bool IsCashAccount(AccountMovement movement, IReadOnlySet<int> cashAccountIds) =>
        cashAccountIds.Contains(movement.LedgerAccountId) || movement.Purpose == LedgerAccountPurpose.ExternalWallet;

    private static (string Category, string Label) CashFlowClassification(JournalEntryType type) => type switch
    {
        JournalEntryType.RideCashCollection => ("operating", "تحصيل نقدي للرحلات"),
        JournalEntryType.RideWalletPayment => ("operating", "مدفوعات الرحلات من المحافظ"),
        JournalEntryType.WalletTopUp => ("operating", "شحن المحافظ"),
        JournalEntryType.RideCancellation => ("operating", "استردادات وإلغاءات الرحلات"),
        JournalEntryType.DriverSettlementCollection => ("operating", "تحصيل تسويات السائقين"),
        JournalEntryType.AdminCashReceipt => ("operating", "قبض نقدي إداري"),
        JournalEntryType.AdminCashDisbursement => ("operating", "صرف نقدي إداري"),
        JournalEntryType.OpeningBalance => ("opening", "أرصدة افتتاحية مسجلة خلال الفترة"),
        _ => ("unclassified", "تسويات أخرى تحتاج تصنيفاً")
    };

    private static decimal Sum(
        IEnumerable<FinancialAccountRow> rows, string currency, LedgerAccountType type) => rows
        .Where(x => string.Equals(x.Currency, currency, StringComparison.OrdinalIgnoreCase) &&
                    x.AccountType == type)
        .Sum(x => x.Amount);

    private static decimal NaturalBalance(LedgerAccountType type, decimal debit, decimal credit) =>
        type is LedgerAccountType.Asset or LedgerAccountType.Expense ? debit - credit : credit - debit;

    private static string NormalizeReportType(string? value)
    {
        var normalized = value?.Trim();
        return normalized switch
        {
            Position or TrialBalance or Income or CashFlow or Equity => normalized,
            _ => throw new ServiceException("financial_report_not_supported", "نوع التقرير المالي غير معروف.")
        };
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length > 12)
            throw new ServiceException("invalid_currency", "رمز العملة غير صالح.");
        return normalized;
    }

    private static (DateTime From, DateTime Until) ResolvePeriod(FinancialStatementsModel model)
    {
        var to = model.DateToUtc?.Date ?? DateTime.UtcNow.Date;
        var from = model.DateFromUtc?.Date ?? new DateTime(to.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (from > to) throw new ServiceException("invalid_financial_report_period", "تاريخ البداية يجب ألا يتجاوز تاريخ النهاية.");
        return (from, to.AddDays(1));
    }

    private static (DateTime? From, DateTime Until) ResolveAsOf(FinancialStatementsModel model)
    {
        var to = model.DateToUtc?.Date ?? DateTime.UtcNow.Date;
        if (model.DateFromUtc is DateTime from && from.Date > to)
            throw new ServiceException("invalid_financial_report_period", "تاريخ البداية يجب ألا يتجاوز تاريخ المركز المالي.");
        return (model.DateFromUtc?.Date, to.AddDays(1));
    }

    private sealed record AccountMovement(
        int LedgerAccountId, string AccountCode, string AccountName, LedgerAccountType AccountType,
        LedgerAccountPurpose Purpose, string Currency, decimal Debit, decimal Credit);
    private sealed record FinancialAccountRow(int LedgerAccountId, string AccountCode, string AccountName,
        LedgerAccountType AccountType, string Currency, decimal Amount);
    private sealed record FinancialSection(string Key, string Title, IReadOnlyCollection<FinancialAccountRow> Rows);
    private sealed record PositionTotal(string Currency, decimal Assets, decimal Liabilities, decimal RecordedEquity,
        decimal RetainedEarnings, decimal TotalEquity, decimal LiabilitiesAndEquity, decimal Difference);
    private sealed record TrialBalanceRow(int LedgerAccountId, string AccountCode, string AccountName, string Currency,
        decimal OpeningDebit, decimal OpeningCredit, decimal Debit, decimal Credit, decimal ClosingDebit, decimal ClosingCredit);
    private sealed record TrialBalanceTotal(string Currency, decimal OpeningDebit, decimal OpeningCredit,
        decimal Debit, decimal Credit, decimal ClosingDebit, decimal ClosingCredit);
    private sealed record IncomeTotal(string Currency, decimal Revenue, decimal Expenses, decimal NetIncome);
    private sealed record CashActivity(string Currency, JournalEntryType EntryType, bool IsReversal, decimal Debit, decimal Credit);
    private sealed record CashFlowActivityRow(string Category, string Operation, string Currency, decimal Inflow, decimal Outflow, decimal Net);
    private sealed record CashFlowTotal(string Currency, decimal OpeningCash, decimal OperatingInflow, decimal OperatingOutflow,
        decimal InvestingNet, decimal FinancingNet, decimal UnclassifiedNet, decimal OpeningBalanceAdjustments,
        decimal NetChange, decimal ClosingCash);
    private sealed record EquityChangeRow(int LedgerAccountId, string AccountCode, string AccountName, string Currency,
        decimal OpeningBalance, decimal Increase, decimal Decrease, decimal PeriodNetIncome, decimal ClosingBalance);
    private sealed record EquityChangeTotal(string Currency, decimal OpeningEquity, decimal Increase, decimal Decrease,
        decimal PeriodNetIncome, decimal ClosingEquity);
}
