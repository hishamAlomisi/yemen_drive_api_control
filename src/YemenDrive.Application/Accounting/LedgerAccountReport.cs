using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Accounting;

public sealed class LedgerAccountReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<LedgerAccountModel>(config)
{
    protected override async Task<object?> ListAsync(LedgerAccountModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        var accounts = await db.LedgerAccounts.AsNoTracking()
            .OrderBy(x => x.Code)
            .Select(x => new
            {
                x.Id, x.Code, x.Name, x.Type, x.Purpose, x.Currency, x.IsActive,
                x.IsSystem, x.IsPosting, x.ParentLedgerAccountId, x.FinancialPartyId,
                financialPartyName = x.FinancialParty == null ? null : x.FinancialParty.Name
            })
            .ToListAsync(token);
        return accounts;
    }
}
