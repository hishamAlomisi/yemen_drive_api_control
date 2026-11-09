using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Rides;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using MessageEntity = YemenDrive.Database.Entities.CommunicationMessage;

namespace YemenDrive.Application.Communication;

public sealed class RideMessage(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser,
    IRideRealtimePublisher? realtimePublisher = null) : OperationsService<RideMessageModel>(config)
{
    protected override async Task<object?> AddAsync(RideMessageModel model, CancellationToken token)
    {
        var senderId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول لإرسال رسالة.");
        if (model.RideId is null || string.IsNullOrWhiteSpace(model.Content))
            throw new ServiceException("invalid_message", "الرحلة ونص الرسالة مطلوبان.");
        if (model.Content.Trim().Length > 4000 || model.MessageType?.Trim().Length > 32)
            throw new ServiceException("invalid_message", "الرسالة أطول من الحد المسموح.");
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.Id == model.RideId.Value, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != senderId && ride.DriverId != senderId)
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية إرسال رسالة لهذه الرحلة.");
        if (ride.DriverId is null || ride.Status < RideStatus.DriverAssigned ||
            ride.Status is RideStatus.Completed or RideStatus.Cancelled)
            throw new ServiceException("chat_not_available", "المحادثة متاحة بعد الاتفاق وإسناد الرحلة فقط.");
        var recipientId = ride.CustomerId == senderId ? ride.DriverId.Value : ride.CustomerId;
        var entity = new MessageEntity
        {
            RideId = ride.Id, SenderId = senderId, RecipientId = recipientId,
            MessageType = string.IsNullOrWhiteSpace(model.MessageType) ? "Text" : model.MessageType.Trim(),
            Content = model.Content.Trim()
        };
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        db.CommunicationMessages.Add(entity);
        await db.SaveChangesAsync(token);
        db.Notifications.Add(new Notification
        {
            UserId = recipientId, Type = NotificationType.System,
            Title = "رسالة جديدة", Body = "لديك رسالة جديدة من الطرف الآخر في الرحلة.",
            DataJson = $"{{\"action\":\"open_ride_chat\",\"rideId\":{ride.Id},\"messageId\":{entity.Id}}}"
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher is not null)
            await realtimePublisher.PublishRideMessageCreatedAsync(
                new RideMessageRealtimeEvent(
                    entity.Id,
                    entity.RideId,
                    entity.SenderId,
                    entity.RecipientId,
                    entity.MessageType,
                    entity.Content,
                    entity.CreatedAtUtc),
                token);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(RideMessageModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول.");
        var batch = model.MessageIds is not null;
        var ids = batch ? model.MessageIds!.Distinct().Order().ToArray()
            : model.Id is int id ? [id] : Array.Empty<int>();
        if (ids.Length is 0 or > 100 || ids.Any(id => id <= 0) || batch && model.RideId is null)
            throw new ServiceException("invalid_message_receipt", "حدد الرسالة أو رسائل رحلة واحدة (حتى 100 رسالة).");
        var markRead = model.MarkRead ?? model.MarkDelivered != true;
        var markDelivered = model.MarkDelivered == true || markRead;
        var messages = new List<MessageEntity>();
        var changed = new List<MessageEntity>();
        // Serialize acknowledgements across devices. A late delivery ACK must not
        // erase a read receipt or replace its first acknowledged timestamp.
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        foreach (var messageId in ids)
        {
            var stored = await db.CommunicationMessages
                .FromSqlInterpolated($"SELECT * FROM [CommunicationMessages] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {messageId}")
                .AsNoTracking().SingleOrDefaultAsync(token)
                ?? throw new ServiceException("message_not_found", "الرسالة غير موجودة.");
            if (stored.RecipientId != userId || model.RideId is int rideId && stored.RideId != rideId)
                throw new ServiceException("message_access_denied", "لا تملك صلاحية تعديل هذه الرسالة.");
            var tracked = db.CommunicationMessages.Local.FirstOrDefault(x => x.Id == messageId);
            if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
            db.Attach(stored);
            messages.Add(stored);
        }
        var now = DateTime.UtcNow;
        foreach (var message in messages)
        {
            var changedReceipt = false;
            if (markDelivered && message.DeliveredAtUtc is null)
            {
                message.DeliveredAtUtc = message.ReadAtUtc ?? now;
                changedReceipt = true;
            }
            if (markRead && message.ReadAtUtc is null)
            {
                message.ReadAtUtc = now;
                changedReceipt = true;
            }
            if (!changedReceipt) continue;
            message.UpdatedAtUtc = now;
            changed.Add(message);
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher is not null)
            foreach (var message in changed)
                await realtimePublisher.PublishRideMessageUpdatedAsync(new(
                    message.Id, message.RideId, message.SenderId, message.RecipientId,
                    message.MessageType, message.Content, message.CreatedAtUtc,
                    message.DeliveredAtUtc, message.ReadAtUtc), token);
        return batch ? messages.Select(ToResult).ToArray() : ToResult(messages[0]);
    }

    private static object ToResult(MessageEntity x) => new
    {
        x.Id, x.RideId, x.SenderId, x.RecipientId, x.MessageType,
        x.Content, x.DeliveredAtUtc, x.ReadAtUtc, x.CreatedAtUtc
    };
}
