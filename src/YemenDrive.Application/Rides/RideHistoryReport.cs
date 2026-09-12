using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed class RideHistoryReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : ReportsService<RideHistoryModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(RideHistoryModel model, CancellationToken cancellationToken)
    {
        // The customer application sees only its own history.  The admin
        // dashboard uses the same report contract, but an active admin must
        // be able to inspect the complete history for every customer/driver.
        var currentUserId = currentUser.UserId;
        var isAdmin = currentUserId is int adminId && await dbContext.Users.AsNoTracking()
            .AnyAsync(x => x.Id == adminId && x.Role == UserRole.Admin && x.IsActive, cancellationToken);

        var query = dbContext.Rides.AsNoTracking().AsQueryable();
        if (!isAdmin)
        {
            var customerId = currentUser.RequireUserId();
            query = query.Where(x => x.CustomerId == customerId);
        }

        if (model.Status is not null && Enum.IsDefined(typeof(RideStatus), model.Status.Value))
            query = query.Where(x => (int)x.Status == model.Status.Value);

        return await query.OrderByDescending(x => x.CreatedAtUtc).Take(200)
            .Select(x => new
            {
                x.Id, x.CustomerId, x.DriverId, x.Status,
                x.ServiceKindId, x.ServiceCatalogItemId,
                pickup = x.PickupAddress,
                destination = x.DestinationAddress,
                x.PickupAddress, x.DestinationAddress,
                x.PickupLatitude, x.PickupLongitude,
                x.DestinationLatitude, x.DestinationLongitude,
                amount = x.CustomerPrice ?? x.ServerPrice ?? 0,
                x.CustomerPrice, x.ServerPrice,
                serviceKindNameAr = x.ServiceKind.NameAr,
                serviceNameAr = x.ServiceCatalogItem.NameAr,
                x.CreatedAtUtc, x.StartedAtUtc, x.CompletedAtUtc
            })
            .ToListAsync(cancellationToken);
    }
}
