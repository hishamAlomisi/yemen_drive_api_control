using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Users;

public sealed class UserReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : ReportsService<UserModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(UserModel model, CancellationToken cancellationToken) =>
        await Query(model, false).Take(200).ToListAsync(cancellationToken);

    protected override async Task<object?> SearchAsync(UserModel model, CancellationToken cancellationToken) =>
        await Query(model, true).Take(200).ToListAsync(cancellationToken);

    private IQueryable<object> Query(UserModel model, bool filter)
    {
        var query = dbContext.Users.AsNoTracking().AsQueryable();
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
            x.Id, x.DisplayName, x.PhoneNumber, x.Email, x.Role, x.IsActive,
            x.City, x.CreatedAtUtc
        });
    }
}
