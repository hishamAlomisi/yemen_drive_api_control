using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Notifications;

public sealed class AdminNotificationCampaignReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    ICurrentUserContext currentUser) : ReportsService<AdminNotificationCampaignModel>(configuration)
{
    protected override async Task<object?> ListAsync(AdminNotificationCampaignModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        var campaigns = db.AdminNotificationCampaigns.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(model.Query))
        {
            var query = model.Query.Trim();
            campaigns = campaigns.Where(x => x.Body.Contains(query));
        }

        return await campaigns.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Take(500)
            .Select(x => new
            {
                x.Id,
                x.Body,
                x.Target,
                x.RecipientCount,
                x.SourceCampaignId,
                x.CreatedAtUtc,
                createdByName = x.CreatedBy.DisplayName
            }).ToListAsync(token);
    }

    protected override Task<object?> SearchAsync(AdminNotificationCampaignModel model, CancellationToken token) =>
        ListAsync(model, token);
}
