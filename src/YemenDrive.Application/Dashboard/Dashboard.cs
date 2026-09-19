using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Dashboard;

public sealed class Dashboard(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : ReportsService<DashboardModel>(configurationStore)
{
    protected override async Task<object?> ReportAsync(DashboardModel model, CancellationToken cancellationToken)
    {
        var users = await dbContext.Users.CountAsync(cancellationToken);
        var drivers = await dbContext.DriverProfiles.CountAsync(cancellationToken);
        var activeRides = await dbContext.Rides.CountAsync(
            x => x.Status != Database.Entities.RideStatus.Completed &&
                 x.Status != Database.Entities.RideStatus.Cancelled,
            cancellationToken);
        var completedRides = await dbContext.Rides.CountAsync(
            x => x.Status == Database.Entities.RideStatus.Completed,
            cancellationToken);
        var walletBalance = await dbContext.Wallets.SumAsync(x => (decimal?)x.Balance, cancellationToken) ?? 0;
        var services = await dbContext.ServiceCatalogItems.CountAsync(x => x.IsActive, cancellationToken);

        var recentRides = await dbContext.Rides.AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.ServiceKindId,
                x.ServiceCatalogItemId,
                pickupDisplayName = x.PickupLabel != "" ? x.PickupLabel : "نقطة الانطلاق",
                destinationDisplayName = x.DestinationLabel != "" ? x.DestinationLabel : "الوجهة",
                x.CustomerPrice,
                x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new { users, drivers, activeRides, completedRides, walletBalance, services, recentRides };
    }
}
