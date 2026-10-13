using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Communication;

public sealed class RideMessageReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<RideMessageModel>(config)
{
    protected override async Task<object?> ListAsync(RideMessageModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول لعرض المحادثة.");
        if (model.RideId is null)
            throw new ServiceException("ride_required", "معرف الرحلة مطلوب.");
        var participant = await db.Rides.AsNoTracking()
            .AnyAsync(x => x.Id == model.RideId.Value && (x.CustomerId == userId || x.DriverId == userId), token);
        if (!participant)
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية الوصول إلى محادثة هذه الرحلة.");
        if ((model.BeforeId is null) != (model.BeforeCreatedAtUtc is null))
            throw new ServiceException(
                "invalid_message_cursor", "مؤشر صفحة المحادثة غير مكتمل.");

        var pageSize = model.PageSize is > 0 ? Math.Min(model.PageSize.Value, 100) : 50;
        var query = db.CommunicationMessages.AsNoTracking()
            .Where(x => x.RideId == model.RideId.Value);
        if (model.BeforeId is int beforeId &&
            model.BeforeCreatedAtUtc is DateTime beforeCreatedAtUtc)
        {
            query = query.Where(x =>
                x.CreatedAtUtc < beforeCreatedAtUtc ||
                (x.CreatedAtUtc == beforeCreatedAtUtc && x.Id < beforeId));
        }

        var messages = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Take(pageSize)
            .Select(x => new { x.Id, x.RideId, x.SenderId, x.RecipientId,
                x.MessageType, x.Content, x.ReadAtUtc, x.CreatedAtUtc })
            .ToListAsync(token);
        messages.Reverse();
        return messages;
    }
}
