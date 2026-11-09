using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Communication;

public sealed record RideChatAuditModel(
    int? DriverId = null, int? CustomerId = null, int? RideId = null,
    int? PageSize = null, int? BeforeRideId = null,
    int? BeforeId = null, DateTime? BeforeCreatedAtUtc = null);

public sealed class RideChatAudit(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : ModelService<RideChatAuditModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["list", "search", "report", "get"];

    protected override async Task<object?> ExecuteOperationAsync(string operation, RideChatAuditModel model, CancellationToken ct)
    {
        await AdministratorAccess.RequireAsync(db, user.RequireUserId(), "operations.read", ct);
        var conversations = db.Rides.AsNoTracking().Where(ride => ride.DriverId != null &&
            db.CommunicationMessages.Any(message => message.RideId == ride.Id &&
                (message.SenderId == ride.DriverId && message.RecipientId == ride.CustomerId ||
                 message.SenderId == ride.CustomerId && message.RecipientId == ride.DriverId)));
        if (operation == "list")
            return await db.Users.AsNoTracking()
                .Where(actor => conversations.Any(ride => ride.DriverId == actor.Id))
                .OrderBy(actor => actor.DisplayName).ThenBy(actor => actor.Id)
                .Select(actor => new
                {
                    actor.Id, actor.DisplayName, actor.PhoneNumber,
                    conversationCount = conversations.Count(ride => ride.DriverId == actor.Id),
                    messageCount = db.CommunicationMessages.Count(message => conversations.Any(ride => ride.Id == message.RideId && ride.DriverId == actor.Id))
                }).ToListAsync(ct);
        if (model.DriverId is not int driverId || driverId <= 0)
            throw new ServiceException("driver_required", "اختر السائق.");
        conversations = conversations.Where(ride => ride.DriverId == driverId);
        if (operation == "search")
            return await db.Users.AsNoTracking()
                .Where(actor => conversations.Any(ride => ride.CustomerId == actor.Id))
                .OrderBy(actor => actor.DisplayName).ThenBy(actor => actor.Id)
                .Select(actor => new
                {
                    actor.Id, actor.DisplayName, actor.PhoneNumber,
                    conversationCount = conversations.Count(ride => ride.CustomerId == actor.Id),
                    messageCount = db.CommunicationMessages.Count(message => conversations.Any(ride => ride.Id == message.RideId && ride.CustomerId == actor.Id))
                }).ToListAsync(ct);
        if (model.CustomerId is not int customerId || customerId <= 0)
            throw new ServiceException("customer_required", "اختر الراكب.");
        conversations = conversations.Where(ride => ride.CustomerId == customerId);
        if (operation == "get")
        {
            if (model.RideId is not int rideId || !await conversations.AnyAsync(ride => ride.Id == rideId, ct))
                throw new ServiceException("conversation_not_found", "لا توجد محادثة لهذه الرحلة بين الطرفين المحددين.");
            return await ReadMessagesAsync(rideId, driverId, customerId, model.PageSize ?? 50,
                model.BeforeId, model.BeforeCreatedAtUtc, ct);
        }
        if (operation != "report")
            throw new ServiceException("operation_not_supported", "العملية غير مدعومة.");
        var pageSize = Math.Clamp(model.PageSize ?? 20, 1, 50);
        if (model.BeforeRideId is int beforeRideId) conversations = conversations.Where(ride => ride.Id < beforeRideId);
        var rides = await conversations.OrderByDescending(ride => ride.Id).Take(pageSize + 1)
            .Select(ride => new
            {
                rideId = ride.Id, ride.CustomerId, customerName = ride.Customer.DisplayName,
                ride.DriverId, driverName = ride.Driver!.DisplayName, ride.Status,
                CreatedAtUtc = DateTime.SpecifyKind(ride.CreatedAtUtc, DateTimeKind.Utc),
                ride.PickupLabel, ride.DestinationLabel
            }).ToListAsync(ct);
        var hasMore = rides.Count > pageSize;
        var result = new List<object>();
        foreach (var ride in rides.Take(pageSize))
        {
            var page = await ReadMessagesAsync(ride.rideId, driverId, customerId, 50, null, null, ct);
            result.Add(new
            {
                ride.rideId, ride.CustomerId, ride.customerName, ride.DriverId, ride.driverName,
                ride.Status, ride.CreatedAtUtc, ride.PickupLabel, ride.DestinationLabel,
                page.MessageCount, page.Messages, page.HasMoreMessages, page.NextBeforeId, page.NextBeforeCreatedAtUtc
            });
        }
        return new { rides = result, hasMore, nextBeforeRideId = hasMore ? rides[pageSize - 1].rideId : (int?)null };
    }

    private async Task<MessagePage> ReadMessagesAsync(int rideId, int driverId, int customerId,
        int pageSize, int? beforeId, DateTime? beforeCreatedAtUtc, CancellationToken ct)
    {
        if ((beforeId is null) != (beforeCreatedAtUtc is null))
            throw new ServiceException("invalid_message_cursor", "مؤشر صفحة المحادثة غير مكتمل.");
        var all = db.CommunicationMessages.AsNoTracking().Where(message => message.RideId == rideId &&
            (message.SenderId == driverId && message.RecipientId == customerId ||
             message.SenderId == customerId && message.RecipientId == driverId));
        var messageCount = await all.CountAsync(ct);
        var query = all;
        if (beforeId is int id && beforeCreatedAtUtc is DateTime timestamp)
        {
            timestamp = timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime()
                : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            query = query.Where(message => message.CreatedAtUtc < timestamp || message.CreatedAtUtc == timestamp && message.Id < id);
        }
        var messages = await query.OrderByDescending(message => message.CreatedAtUtc).ThenByDescending(message => message.Id)
            .Take(Math.Clamp(pageSize, 1, 100) + 1).ToListAsync(ct);
        var hasMore = messages.Count > Math.Clamp(pageSize, 1, 100);
        if (hasMore) messages.RemoveAt(messages.Count - 1);
        messages.Reverse();
        var first = messages.FirstOrDefault();
        var preceding = first is null ? 0 : await all.CountAsync(message =>
            message.CreatedAtUtc < first.CreatedAtUtc || message.CreatedAtUtc == first.CreatedAtUtc && message.Id < first.Id, ct);
        var rows = messages.Select((message, index) => (object)new
        {
            message.Id, message.RideId, message.SenderId, message.RecipientId,
            senderRole = message.SenderId == driverId ? "driver" : "customer",
            sequence = preceding + index + 1,
            message.MessageType, message.Content, message.CreatedAtUtc, message.DeliveredAtUtc, message.ReadAtUtc
        }).ToArray();
        return new(rideId, messageCount, rows, hasMore, hasMore ? first?.Id : null,
            hasMore ? first?.CreatedAtUtc : null);
    }

    private sealed record MessagePage(int RideId, int MessageCount, object[] Messages,
        bool HasMoreMessages, int? NextBeforeId, DateTime? NextBeforeCreatedAtUtc);
}
