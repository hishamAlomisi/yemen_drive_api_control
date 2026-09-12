using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Notifications;

public sealed record NotificationModel(
    int? Id = null,
    int? UserId = null,
    NotificationType? Type = null,
    string? Title = null,
    string? Body = null,
    bool? IsRead = null);
