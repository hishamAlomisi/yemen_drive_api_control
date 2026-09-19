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
    protected override Task<object?> AddAsync(WalletModel model, CancellationToken cancellationToken)
    {
        return Task.FromException<object?>(new ServiceException("wallet_direct_mutation_not_supported",
            "لا يمكن إنشاء حركة محفظة مباشرة. استخدم عملية دفع أو استرداد أو طلب شحن مؤكد."));
    }

    protected override async Task<object?> GetAsync(WalletModel model, CancellationToken cancellationToken)
    {
        var userId = await ResolveReadableUserIdAsync(model.UserId, cancellationToken);
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

    private async Task<int> ResolveReadableUserIdAsync(int? requestedUserId, CancellationToken token)
    {
        var currentUserId = currentUser.RequireUserId();
        if (requestedUserId is null || requestedUserId == currentUserId) return currentUserId;
        var isAdmin = await dbContext.Users.AnyAsync(x => x.Id == currentUserId &&
            x.Role == UserRole.Admin && x.IsActive, token);
        if (!isAdmin)
            throw new ServiceException("wallet_access_denied", "لا يمكنك الاطلاع على محفظة مستخدم آخر.");
        return requestedUserId.Value;
    }
}
