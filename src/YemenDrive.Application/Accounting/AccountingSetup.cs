using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Accounting;

public sealed class AccountingSetup(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser,
    FinancialAccountProvisioningService financialAccounts) : OperationsService<AccountingSetupModel>(config)
{
    protected override async Task<object?> AddAsync(AccountingSetupModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var provisioned = await financialAccounts.ProvisionExistingAsync(token);
        await transaction.CommitAsync(token);
        return new
        {
            created = provisioned.LedgerAccountsCreated,
            financialPartiesCreated = provisioned.FinancialPartiesCreated,
            provisioned.UsersProcessed,
            provisioned.ServiceKindsProcessed
        };
    }
}
