using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
namespace YemenDrive.Application.Wallets;

public sealed record WalletLedgerReconciliationModel(int WalletId = 0, string Note = "");
public sealed class WalletLedgerReconciliation(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext actor) : ModelService<WalletLedgerReconciliationModel>(config)
{
    public override IReadOnlyCollection<string> Operations => ["resolveDebt"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, WalletLedgerReconciliationModel model, CancellationToken ct)
    {
        await AdministratorAccess.RequireAsync(db, actor.RequireUserId(), "finance.write", ct);
        if (model.WalletId <= 0 || model.Note.Trim().Length < 8 || model.Note.Length > 1000)
            throw new ServiceException("wallet_debt_approval_required", "حدد المحفظة وسبب اعتماد المديونية بوضوح.");
        if (!await db.Wallets.AnyAsync(x => x.Id == model.WalletId, ct)) throw new ServiceException("wallet_not_found", "المحفظة غير موجودة.");
        var sync = new WalletLedgerSynchronization(db);
        var applied = await sync.ReconcileExistingAsync(ct, new HashSet<int> { model.WalletId }, actor.RequireUserId(), model.Note.Trim());
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(x => x.Id == model.WalletId, ct);
        return new { wallet.Id, wallet.Balance, wallet.LedgerDebt, netBalance = wallet.Balance - wallet.LedgerDebt, applied, reviewMessages = sync.ReviewMessages };
    }
}
