using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Accounting;

/// <summary>Read-only financial statement for the authenticated driver.</summary>
public sealed class DriverFinancialReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<DriverFinancialReportModel>(config)
{
    protected override async Task<object?> ListAsync(DriverFinancialReportModel model, CancellationToken token)
    {
        var driverId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        var driver = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == driverId && x.Role == UserRole.Driver && x.IsActive, token)
            ?? throw new ServiceException("driver_required", "هذا التقرير متاح للسائق فقط.");

        var baseQuery = db.JournalLines.AsNoTracking()
            .Include(x => x.LedgerAccount)
            .Include(x => x.JournalEntry)
            .Where(x => x.UserId == driver.Id ||
                        (x.LedgerAccount.FinancialParty != null &&
                         x.LedgerAccount.FinancialParty.Type == FinancialPartyType.User &&
                         x.LedgerAccount.FinancialParty.EntityId == driver.Id));
        var summaryRows = await baseQuery.Where(x => x.JournalEntry.OccurredAtUtc <= DateTime.UtcNow)
            .Select(x => new { x.Debit, x.Credit, accountPurpose = x.LedgerAccount.Purpose })
            .ToListAsync(token);

        var from = model.From?.Date;
        var toExclusive = model.To?.Date.AddDays(1);
        var query = baseQuery;
        if (from is not null) query = query.Where(x => x.JournalEntry.OccurredAtUtc >= from.Value);
        if (toExclusive is not null) query = query.Where(x => x.JournalEntry.OccurredAtUtc < toExclusive.Value);
        if (model.EntryType is JournalEntryType entryType) query = query.Where(x => x.JournalEntry.Type == entryType);
        if (!string.IsNullOrWhiteSpace(model.Query))
        {
            var search = model.Query.Trim();
            query = query.Where(x => x.Description.Contains(search) || x.JournalEntry.Description.Contains(search) || x.JournalEntry.Reference.Contains(search));
        }

        var rows = await query.OrderByDescending(x => x.JournalEntry.OccurredAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new
            {
                x.Id, x.Debit, x.Credit, x.Description, x.RideId,
                x.JournalEntry.OccurredAtUtc,
                entryType = x.JournalEntry.Type,
                accountName = x.LedgerAccount.Name,
                accountPurpose = x.LedgerAccount.Purpose
            }).ToListAsync(token);
        var balance = summaryRows.Sum(x => x.Debit - x.Credit);
        var commission = summaryRows.Where(x => x.accountPurpose == LedgerAccountPurpose.DriverCommission).Sum(x => x.Debit - x.Credit);
        return new { balance, totalCommission = commission, transactions = rows };
    }
}

public sealed record DriverFinancialReportModel(
    DateTime? From = null,
    DateTime? To = null,
    string? Query = null,
    JournalEntryType? EntryType = null);
