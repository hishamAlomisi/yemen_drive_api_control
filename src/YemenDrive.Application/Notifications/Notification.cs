using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Notifications;

public sealed class Notification(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<NotificationModel>(config)
{
    protected override async Task<object?> UpdateAsync(NotificationModel model, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف الإشعار مطلوب.");
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        var entity = await db.Notifications.SingleOrDefaultAsync(x => x.Id == model.Id && x.UserId == userId, token)
            ?? throw new ServiceException("notification_not_found", "الإشعار غير موجود.");
        if (model.IsRead is not null) entity.IsRead = model.IsRead.Value;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        return new { entity.Id, entity.IsRead, entity.UpdatedAtUtc };
    }
}
