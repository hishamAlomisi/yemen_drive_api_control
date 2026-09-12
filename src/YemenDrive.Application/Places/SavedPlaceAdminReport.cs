using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Places;

public sealed class SavedPlaceAdminReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config) : ReportsService<SavedPlaceAdminModel>(config)
{
    protected override async Task<object?> ListAsync(SavedPlaceAdminModel model, CancellationToken token) =>
        await db.SavedPlaces.AsNoTracking()
            .Where(x => model.UserId == null || x.UserId == model.UserId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(500)
            .Select(x => new
            {
                x.Id,
                x.UserId,
                userName = x.User.DisplayName,
                x.Label,
                x.Kind,
                x.Address,
                x.Latitude,
                x.Longitude,
                x.CreatedAtUtc,
                x.UpdatedAtUtc
            })
            .ToListAsync(token);
}
