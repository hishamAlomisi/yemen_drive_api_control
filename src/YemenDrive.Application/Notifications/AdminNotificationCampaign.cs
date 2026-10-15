using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using CampaignEntity = YemenDrive.Database.Entities.AdminNotificationCampaign;
using NotificationEntity = YemenDrive.Database.Entities.Notification;

namespace YemenDrive.Application.Notifications;

public sealed class AdminNotificationCampaign(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    ICurrentUserContext currentUser) : OperationsService<AdminNotificationCampaignModel>(configuration)
{
    protected override async Task<object?> AddAsync(AdminNotificationCampaignModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        var adminId = currentUser.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول بحساب الإدارة.");
        var body = model.Body?.Trim();
        if (string.IsNullOrWhiteSpace(body))
            throw new ServiceException("notification_body_required", "اكتب نص الإشعار أولاً.");
        if (body.Length > 700)
            throw new ServiceException("notification_body_too_long", "الحد الأقصى لنص الإشعار 700 حرف.");
        if (model.Target is not int targetValue || !Enum.IsDefined(typeof(NotificationCampaignTarget), targetValue))
            throw new ServiceException("notification_target_required", "اختر العملاء أو السائقين أو الجميع.");

        var target = (NotificationCampaignTarget)targetValue;
        var recipientsQuery = db.Users.AsNoTracking().Where(user => user.IsActive);
        recipientsQuery = target switch
        {
            NotificationCampaignTarget.Customers => recipientsQuery.Where(user => user.Role == UserRole.Customer),
            NotificationCampaignTarget.Drivers => recipientsQuery.Where(user => user.Role == UserRole.Driver),
            NotificationCampaignTarget.Everyone => recipientsQuery.Where(user =>
                user.Role == UserRole.Customer || user.Role == UserRole.Driver),
            _ => throw new ServiceException("notification_target_invalid", "الفئة المستهدفة غير صحيحة.")
        };
        var recipientIds = await recipientsQuery.OrderBy(user => user.Id)
            .Select(user => user.Id).ToArrayAsync(token);
        if (recipientIds.Length == 0)
            throw new ServiceException("notification_target_empty", "لا توجد حسابات نشطة ضمن الفئة المختارة.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var campaign = new CampaignEntity
        {
            Body = body,
            Target = target,
            RecipientCount = recipientIds.Length,
            CreatedByUserId = adminId,
            SourceCampaignId = model.SourceCampaignId
        };
        db.AdminNotificationCampaigns.Add(campaign);
        await db.SaveChangesAsync(token);

        foreach (var batch in recipientIds.Chunk(400))
        {
            db.Notifications.AddRange(batch.Select(userId => new NotificationEntity
            {
                UserId = userId,
                Type = NotificationType.System,
                Title = "إشعار من يمن درايف",
                Body = body,
                DataJson = $"{{\"action\":\"open_notifications\",\"campaignId\":{campaign.Id}}}",
                CreatedAtUtc = DateTime.UtcNow
            }));
            await db.SaveChangesAsync(token);
        }

        await transaction.CommitAsync(token);
        return new
        {
            campaign.Id,
            campaign.Body,
            campaign.Target,
            campaign.RecipientCount,
            campaign.SourceCampaignId,
            campaign.CreatedAtUtc
        };
    }

    protected override async Task<object?> DeleteAsync(AdminNotificationCampaignModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        if (model.Id is not int id)
            throw new ServiceException("campaign_id_required", "معرف سجل الإرسال مطلوب.");
        var campaign = await db.AdminNotificationCampaigns.SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException("campaign_not_found", "سجل الإرسال غير موجود.");
        db.AdminNotificationCampaigns.Remove(campaign);
        await db.SaveChangesAsync(token);
        return new { campaign.Id };
    }
}
