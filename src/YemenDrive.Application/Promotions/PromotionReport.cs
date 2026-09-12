using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Promotions;

public sealed class PromotionReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config) : ReportsService<PromotionModel>(config)
{
    protected override async Task<object?> ListAsync(PromotionModel model, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        return await db.Promotions.AsNoTracking()
            .Where(x => model.IsActive != false && x.IsActive &&
                        x.StartsAtUtc <= now && x.EndsAtUtc >= now &&
                        (x.UsageLimit == null || x.UsageCount < x.UsageLimit) &&
                        (string.IsNullOrWhiteSpace(model.Code) || x.Code == model.Code))
            .OrderBy(x => x.EndsAtUtc)
            .Select(x => new
            {
                x.Id,
                x.Code,
                title = x.Name,
                name = x.Name,
                description = x.Name,
                x.FixedDiscount,
                x.PercentageDiscount,
                x.MaximumDiscount,
                x.StartsAtUtc,
                x.EndsAtUtc,
                expiresAtUtc = x.EndsAtUtc,
                discount = x.PercentageDiscount != null
                    ? $"خصم {x.PercentageDiscount}%"
                    : x.FixedDiscount != null
                        ? $"خصم {x.FixedDiscount}"
                        : string.Empty,
                x.UsageLimit,
                x.UsageCount,
                x.IsActive
            })
            .ToListAsync(token);
    }
}
