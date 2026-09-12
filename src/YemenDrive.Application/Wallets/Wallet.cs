using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Wallets;

public sealed class Wallet(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : OperationsService<WalletModel>(configurationStore)
{
    protected override async Task<object?> AddAsync(WalletModel model, CancellationToken cancellationToken)
    {
        if (model.Amount <= 0 || string.IsNullOrWhiteSpace(model.Description))
            throw new ServiceException("invalid_transaction", "المبلغ والوصف مطلوبان.");

        // The token wins for client requests; an explicit id is supported for
        // the administration console, which has no customer session.
        var userId = currentUser.UserId ?? model.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول لتنفيذ هذه العملية.");
        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var wallet = await dbContext.Wallets.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? throw new ServiceException("wallet_not_found", "محفظة المستخدم غير موجودة.");
        var increasesBalance = model.Type is WalletTransactionType.Credit or WalletTransactionType.Release or WalletTransactionType.Refund;
        var nextBalance = increasesBalance ? wallet.Balance + model.Amount : wallet.Balance - model.Amount;
        if (nextBalance < 0)
            throw new ServiceException("insufficient_balance", "رصيد المحفظة غير كافٍ.");

        wallet.Balance = nextBalance;
        wallet.UpdatedAtUtc = DateTime.UtcNow;
        var transaction = new WalletTransaction
        {
            WalletId = wallet.Id,
            RideId = model.RideId,
            Type = model.Type,
            Amount = model.Amount,
            BalanceAfter = nextBalance,
            Description = model.Description.Trim(),
            ExternalReference = model.ExternalReference
        };
        dbContext.WalletTransactions.Add(transaction);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await databaseTransaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            throw new ServiceException("wallet_concurrency_conflict", "تغير رصيد المحفظة أثناء تنفيذ العملية. أعد المحاولة.");
        }
        return new { transaction.Id, transaction.Type, transaction.Amount, transaction.BalanceAfter, wallet.Currency };
    }

    protected override async Task<object?> GetAsync(WalletModel model, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? model.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول لتنفيذ هذه العملية.");
        var wallet = await dbContext.Wallets.AsNoTracking().Include(x => x.Transactions)
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? throw new ServiceException("wallet_not_found", "محفظة المستخدم غير موجودة.");
        return new
        {
            wallet.Id,
            wallet.UserId,
            wallet.Balance,
            wallet.Currency,
            transactions = wallet.Transactions.OrderByDescending(x => x.CreatedAtUtc).Select(x => new
            {
                x.Id, x.Type, x.Amount, x.BalanceAfter, x.Description, x.ExternalReference, x.CreatedAtUtc
            })
        };
    }
}
