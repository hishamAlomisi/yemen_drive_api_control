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
        var userId = await ResolveReadableUserIdAsync(model.UserId, model.PhoneNumber, cancellationToken);
        var wallet = await dbContext.Wallets.AsNoTracking()
            .Include(x => x.User)
            .Include(x => x.Transactions)
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? throw new ServiceException("wallet_not_found", string.IsNullOrWhiteSpace(model.PhoneNumber)
                ? "محفظة المستخدم غير موجودة."
                : "لا توجد محفظة مرتبطة برقم الهاتف المدخل.");
        return new
        {
            wallet.Id,
            wallet.UserId,
            wallet.Balance,
            wallet.Currency,
            owner = new
            {
                wallet.User.Id,
                wallet.User.DisplayName,
                wallet.User.PhoneNumber,
                wallet.User.Email,
                wallet.User.Gender,
                wallet.User.Street,
                wallet.User.City,
                wallet.User.District,
                wallet.User.Role,
                wallet.User.IsActive,
                wallet.User.CreatedAtUtc,
                wallet.User.UpdatedAtUtc
            },
            transactions = wallet.Transactions.OrderByDescending(x => x.CreatedAtUtc).Select(x => new
            {
                x.Id, x.Type, x.Amount, x.BalanceAfter, x.Description, x.ExternalReference, x.CreatedAtUtc
            })
        };
    }

    private async Task<int> ResolveReadableUserIdAsync(int? requestedUserId, string? phoneNumber, CancellationToken token)
    {
        var currentUserId = currentUser.RequireUserId();
        int targetUserId;
        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var phone = phoneNumber.Trim();
            var currentPhone = await dbContext.Users.AsNoTracking()
                .Where(x => x.Id == currentUserId)
                .Select(x => x.PhoneNumber)
                .SingleOrDefaultAsync(token);
            if (currentPhone == phone) return currentUserId;
            if (!await IsAdminAsync(currentUserId, token))
                throw new ServiceException("wallet_access_denied", "لا يمكنك الاطلاع على محفظة مستخدم آخر.");

            targetUserId = await dbContext.Users.AsNoTracking()
                .Where(x => x.PhoneNumber == phone)
                .Select(x => (int?)x.Id)
                .SingleOrDefaultAsync(token)
                ?? throw new ServiceException("wallet_not_found", "لا توجد محفظة مرتبطة برقم الهاتف المدخل.");
        }
        else
        {
            targetUserId = requestedUserId ?? currentUserId;
        }

        if (targetUserId == currentUserId) return currentUserId;
        if (!await IsAdminAsync(currentUserId, token))
            throw new ServiceException("wallet_access_denied", "لا يمكنك الاطلاع على محفظة مستخدم آخر.");
        return targetUserId;
    }

    private Task<bool> IsAdminAsync(int userId, CancellationToken token) =>
        dbContext.Users.AnyAsync(x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, token);
}
