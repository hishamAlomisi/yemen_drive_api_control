using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Operations;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Support;

public sealed class Referral(
    DatabaseConfigurationStore config) : OperationsService<ReferralModel>(config)
{
    protected override Task<object?> AddAsync(ReferralModel model, CancellationToken token)
    {
        throw new ServiceException("referral_unavailable", "خدمة الإحالة ومكافآتها غير متاحة حتى اعتماد آليتها وتفعيلها بالكامل.");
    }
}

public sealed class ReferralReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<ReferralModel>(config)
{
    protected override async Task<object?> ListAsync(ReferralModel model, CancellationToken token)
    {
        var userId = currentUser.RequireUserId();
        return await db.ReferralRedemptions.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(100)
            .Select(x => new { x.Id, x.UserId, x.Code, x.Status, x.CreatedAtUtc })
            .ToListAsync(token);
    }
}
