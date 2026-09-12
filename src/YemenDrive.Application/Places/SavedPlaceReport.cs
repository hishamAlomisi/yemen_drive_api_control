using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Places;

public sealed class SavedPlaceReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<SavedPlaceModel>(config)
{
    protected override async Task<object?> ListAsync(SavedPlaceModel model, CancellationToken token)
    {
        var userId = currentUser.RequireUserId();
        return await db.SavedPlaces.AsNoTracking().Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAtUtc).Take(500)
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
}
