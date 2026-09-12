using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Operations;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using ReferralEntity = YemenDrive.Database.Entities.ReferralRedemption;

namespace YemenDrive.Application.Support;

public sealed class Referral(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<ReferralModel>(config)
{
    protected override async Task<object?> AddAsync(ReferralModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? model.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        if (string.IsNullOrWhiteSpace(model.Code))
            throw new ServiceException("code_required", "رمز الإحالة مطلوب.");
        var entity = new ReferralEntity { UserId = userId, Code = model.Code.Trim(), Status = "Submitted" };
        db.ReferralRedemptions.Add(entity);
        await db.SaveChangesAsync(token);
        return new { entity.Id, entity.UserId, entity.Code, entity.Status, entity.CreatedAtUtc };
    }
}

public sealed class ReferralReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<ReferralModel>(config)
{
    protected override async Task<object?> ListAsync(ReferralModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? model.UserId
            ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        return await db.ReferralRedemptions.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(100)
            .Select(x => new { x.Id, x.UserId, x.Code, x.Status, x.CreatedAtUtc })
            .ToListAsync(token);
    }
}
