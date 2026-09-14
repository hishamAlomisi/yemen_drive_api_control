using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Accounting;

internal static class AccountingAccess
{
    public static async Task<int> RequireAdminAsync(
        YemenDriveDbContext db,
        ICurrentUserContext currentUser,
        CancellationToken token)
    {
        var userId = currentUser.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كمدير.");
        var isAdmin = await db.Users.AsNoTracking().AnyAsync(
            x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, token);
        if (!isAdmin)
            throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");
        return userId;
    }
}
