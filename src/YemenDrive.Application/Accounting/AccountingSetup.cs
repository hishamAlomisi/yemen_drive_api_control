using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Security;
using LedgerAccountEntity = YemenDrive.Database.Entities.LedgerAccount;

namespace YemenDrive.Application.Accounting;

public sealed class AccountingSetup(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<AccountingSetupModel>(config)
{
    protected override async Task<object?> AddAsync(AccountingSetupModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        var definitions = new[]
        {
            ("1100", "ذمم السائقين", LedgerAccountType.Asset, LedgerAccountPurpose.DriverCurrentAccount, false),
            ("1200", "حسابات تسوية المحافظ الخارجية", LedgerAccountType.Asset, LedgerAccountPurpose.ExternalWallet, true),
            ("2100", "التزامات محافظ العملاء", LedgerAccountType.Liability, LedgerAccountPurpose.CustomerWallet, false),
            ("4100", "رسوم الخدمة", LedgerAccountType.Revenue, LedgerAccountPurpose.ServiceFee, true),
            ("4200", "تحصيل الخدمة", LedgerAccountType.Revenue, LedgerAccountPurpose.ServiceCollection, true),
            ("5100", "عمولات السائقين", LedgerAccountType.Expense, LedgerAccountPurpose.DriverCommission, true)
        };
        var existingCodes = await db.LedgerAccounts.Select(x => x.Code).ToListAsync(token);
        var additions = definitions.Where(x => !existingCodes.Contains(x.Item1, StringComparer.OrdinalIgnoreCase))
            .Select(x => new LedgerAccountEntity
            {
                Code = x.Item1, Name = x.Item2, Type = x.Item3, Purpose = x.Item4,
                Currency = "YER", IsActive = true, IsSystem = true, IsPosting = x.Item5
            }).ToArray();
        if (additions.Length > 0)
        {
            db.LedgerAccounts.AddRange(additions);
            await db.SaveChangesAsync(token);
        }
        return new { created = additions.Length, accounts = additions.Select(LedgerAccount.ToResult).ToArray() };
    }
}
