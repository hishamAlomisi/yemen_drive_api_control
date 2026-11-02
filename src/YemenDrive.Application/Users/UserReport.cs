using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Users;

public sealed class UserReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore, ICurrentUserContext currentUser) : ReportsService<UserModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(UserModel model, CancellationToken cancellationToken) =>
        await Query(model, false).Take(200).ToListAsync(cancellationToken);

    protected override async Task<object?> SearchAsync(UserModel model, CancellationToken cancellationToken) =>
        await Query(model, true).Take(200).ToListAsync(cancellationToken);

    private IQueryable<object> Query(UserModel model, bool filter)
    {
        var actor = currentUser.RequireUserId();
        var admin = dbContext.Users.Any(x => x.Id == actor && x.IsActive && x.Role == UserRole.Admin);
        var query = dbContext.Users.AsNoTracking().AsQueryable();
        if (!admin) query = query.Where(x => x.Id == actor && x.IsActive);
        if (filter)
        {
            var term = (model.DisplayName ?? model.PhoneNumber ?? model.Email)?.Trim();
            if (!string.IsNullOrWhiteSpace(term))
                query = query.Where(x => (x.DisplayName != null && x.DisplayName.Contains(term)) ||
                                         x.PhoneNumber.Contains(term) ||
                                         (x.Email != null && x.Email.Contains(term)));
        }

        return query.OrderByDescending(x => x.CreatedAtUtc).Select(x => new
        {
            x.Id, x.DisplayName, x.PhoneNumber, x.Email, x.Role, x.IsActive, x.IsSystemAdministrator,
            x.Gender, x.City, x.Street, x.District, x.CreatedAtUtc, x.UpdatedAtUtc
        });
    }
}
