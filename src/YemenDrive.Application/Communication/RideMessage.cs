using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using MessageEntity = YemenDrive.Database.Entities.CommunicationMessage;

namespace YemenDrive.Application.Communication;

public sealed class RideMessage(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<RideMessageModel>(config)
{
    protected override async Task<object?> AddAsync(RideMessageModel model, CancellationToken token)
    {
        var senderId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول لإرسال رسالة.");
        if (model.RideId is null || string.IsNullOrWhiteSpace(model.Content))
            throw new ServiceException("invalid_message", "الرحلة ونص الرسالة مطلوبان.");
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
        db.CommunicationMessages.Add(entity);
        db.Notifications.Add(new Notification
        {
            UserId = recipientId, Type = NotificationType.System,
            Title = "رسالة جديدة", Body = "لديك رسالة جديدة من الطرف الآخر في الرحلة.",
            DataJson = $"{{\"rideId\":{ride.Id}}}"
        });
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(RideMessageModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول.");
        var entity = await db.CommunicationMessages.SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("message_not_found", "الرسالة غير موجودة.");
        if (entity.RecipientId != userId)
            throw new ServiceException("message_access_denied", "لا تملك صلاحية تعديل هذه الرسالة.");
        entity.ReadAtUtc = model.MarkRead == false ? null : DateTime.UtcNow;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    private static object ToResult(MessageEntity x) => new
    {
        x.Id, x.RideId, x.SenderId, x.RecipientId, x.MessageType,
        x.Content, x.ReadAtUtc, x.CreatedAtUtc
    };
}
