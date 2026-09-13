using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed class RideReport(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : ReportsService<RideModel>(configurationStore)
{
    protected override async Task<object?> ListAsync(RideModel model, CancellationToken cancellationToken)
    {
        var query = dbContext.Rides.AsNoTracking().AsQueryable();
        var currentUserId = currentUser.UserId;
        var isAdmin = currentUserId is int adminId && await dbContext.Users.AsNoTracking()
            .AnyAsync(x => x.Id == adminId && x.Role == UserRole.Admin && x.IsActive, cancellationToken);
        DriverProfile? driverProfile = null;
        if (!isAdmin && currentUserId is int userId)
        {
            driverProfile = await dbContext.DriverProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);

            if (driverProfile is null)
            {
                query = query.Where(x => x.CustomerId == userId || x.DriverId == userId);
            }
            else
            {
                // A driver can see rides matching their assigned service while
                // they are still open for offers, in addition to their rides.
                query = query.Where(x =>
                    x.DriverId == userId ||
                    (x.DriverId == null &&
                     (x.Status == RideStatus.Searching || x.Status == RideStatus.Negotiating) &&
                     x.ServiceKindId == driverProfile.ServiceKindId &&
                     x.ServiceCatalogItemId == driverProfile.ServiceCatalogItemId));
            }
        }
        if (!isAdmin && currentUserId is null && model.CustomerId is not null)
            query = query.Where(x => x.CustomerId == model.CustomerId);
        if (model.Status is not null) query = query.Where(x => x.Status == model.Status);
        var driverOfferUserId = driverProfile?.UserId;

        return await query.OrderByDescending(x => x.CreatedAtUtc)
            .Take(200)
            .Select(x => new
            {
                x.Id, x.CustomerId, customerName = x.Customer.DisplayName,
                customerPhone = x.Customer.PhoneNumber, x.DriverId,
                driverName = x.Driver != null ? x.Driver.DisplayName : null,
                x.Status, x.ServiceKindId, x.ServiceCatalogItemId,
                serviceKindCode = x.ServiceKind.Code, serviceKindNameAr = x.ServiceKind.NameAr,
                serviceCode = x.ServiceCatalogItem.Code, serviceNameAr = x.ServiceCatalogItem.NameAr,
                x.PickupAddress, x.DestinationAddress, x.CustomerPrice, x.CreatedAtUtc,
                driverOfferAmount = !isAdmin && driverOfferUserId.HasValue
                    ? x.Offers.Where(offer => offer.DriverId == driverOfferUserId.Value)
                        .OrderByDescending(offer => offer.CreatedAtUtc)
                        .Select(offer => (decimal?)offer.Amount).FirstOrDefault()
                    : null,
                driverOfferExpiresAtUtc = !isAdmin && driverOfferUserId.HasValue
                    ? x.Offers.Where(offer => offer.DriverId == driverOfferUserId.Value)
                        .OrderByDescending(offer => offer.CreatedAtUtc)
                        .Select(offer => (DateTime?)offer.ExpiresAtUtc).FirstOrDefault()
                    : null,
                driverOfferStatus = !isAdmin && driverOfferUserId.HasValue
                    ? x.Offers.Where(offer => offer.DriverId == driverOfferUserId.Value)
                        .OrderByDescending(offer => offer.CreatedAtUtc)
                        .Select(offer => (OfferStatus?)offer.Status).FirstOrDefault()
                    : null
            })
            .ToListAsync(cancellationToken);
    }
}
