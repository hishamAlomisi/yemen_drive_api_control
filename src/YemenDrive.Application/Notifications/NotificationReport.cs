using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Notifications;

public sealed class NotificationReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<NotificationModel>(config)
{
    protected override async Task<object?> ListAsync(NotificationModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? model.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        return await db.Notifications.AsNoTracking()
            .Where(x => x.UserId == userId && (model.IsRead == null || x.IsRead == model.IsRead))
            .OrderByDescending(x => x.CreatedAtUtc).Take(200)
            .Select(x => new { x.Id, x.UserId, x.Type, x.Title, x.Body, x.DataJson, x.IsRead, x.CreatedAtUtc })
            .ToListAsync(token);
    }
}
