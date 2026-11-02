using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Drivers;

public sealed class DriverReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore, ICurrentUserContext currentUser) : ReportsService<DriverModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(DriverModel model, CancellationToken cancellationToken) =>
        await dbContext.DriverProfiles.AsNoTracking()
            .Where(x => x.UserId == currentUser.UserId || dbContext.Users.Any(u => u.Id == currentUser.UserId && u.IsActive && u.Role == UserRole.Admin))
            .Include(x => x.ServiceKind).Include(x => x.ServiceCatalogItem)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(200)
            .Select(x => new
            {
                x.Id, x.UserId, x.User.DisplayName, x.User.PhoneNumber, x.ServiceKindId, x.ServiceCatalogItemId,
                serviceKindCode = x.ServiceKind.Code, serviceKindNameAr = x.ServiceKind.NameAr,
                serviceCode = x.ServiceCatalogItem.Code, serviceNameAr = x.ServiceCatalogItem.NameAr,
                x.VehicleModel, x.PlateNumber,
                isActive = x.User.IsActive,
                walletId = dbContext.Wallets.Where(wallet => wallet.UserId == x.UserId)
                    .Select(wallet => (int?)wallet.Id).FirstOrDefault(),
                x.CommissionRate, x.IsAvailable, x.IsApproved, x.AcceptsNewRequests, x.ApprovedPassengerSeats,
                rating = dbContext.DriverRatings.Where(r => r.DriverUserId == x.UserId)
                    .Select(r => (decimal?)r.Score).Average() ?? 0m,
                x.PhotoUrl, x.CreatedAtUtc, x.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);
}
