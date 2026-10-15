using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
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
                financialPartyName = x.FinancialParty == null ? null : x.FinancialParty.Name,
                financialPartyPhoneNumber = x.FinancialParty == null || x.FinancialParty.EntityId == null
                    ? null
                    : x.FinancialParty.Type == FinancialPartyType.User
                        ? db.Users.Where(user => user.Id == x.FinancialParty.EntityId.Value)
                            .Select(user => user.PhoneNumber).FirstOrDefault()
                        : x.FinancialParty.Type == FinancialPartyType.Wallet
                            ? db.Wallets.Where(wallet => wallet.Id == x.FinancialParty.EntityId.Value)
                                .Select(wallet => wallet.User.PhoneNumber).FirstOrDefault()
                            : null
            })
            .ToListAsync(token);
        return accounts;
    }
}
