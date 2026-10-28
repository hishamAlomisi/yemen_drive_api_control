using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Rides;
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
        var notifications = await db.Notifications.AsNoTracking()
            .Where(x => x.UserId == userId && (model.IsRead == null || x.IsRead == model.IsRead))
            .OrderByDescending(x => x.CreatedAtUtc).Take(200)
            .ToListAsync(token);
        var eligible = await RideRequestNotificationPolicy.EligibleRideIdsAsync(db, userId,
            notifications.Select(RideRequestNotificationPolicy.RequestRideId).OfType<int>(), token);
        return notifications.Where(notification => RideRequestNotificationPolicy.RequestRideId(notification) is not int rideId ||
                eligible.Contains(rideId))
            .Select(x => new { x.Id, x.UserId, x.Type, x.Title, x.Body, x.DataJson, x.IsRead, x.CreatedAtUtc })
            .ToArray();
    }
}
