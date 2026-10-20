using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record RideSearchViewModel(int? RideId = null, decimal? CustomerPrice = null);
public sealed record RideSearchViewer(int RideId, int DriverId, string Name, string? PhotoUrl, DateTime ViewedAtUtc);

public sealed class RideSearchView(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser,
    IRideRealtimePublisher? realtimePublisher = null) : OperationsService<RideSearchViewModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "update"];

    protected override async Task<object?> GetAsync(RideSearchViewModel model, CancellationToken token)
    {
        var ride = await FindAsync(model.RideId, token);
        if (currentUser.UserId != ride.CustomerId && !await db.Users.AsNoTracking().AnyAsync(
                x => x.Id == currentUser.UserId && x.Role == UserRole.Admin && x.IsActive, token))
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية عرض مشاهدي هذا الطلب.");
        return await db.RideSearchViews.AsNoTracking().Where(x => x.RideId == ride.Id)
            .OrderBy(x => x.ViewedAtUtc)
            .Select(x => new RideSearchViewer(x.RideId, x.DriverId, x.Driver.DisplayName ?? string.Empty,
                x.Driver.DriverProfile == null ? null : x.Driver.DriverProfile.PhotoUrl, x.ViewedAtUtc))
            .ToListAsync(token);
    }

    protected override async Task<object?> UpdateAsync(RideSearchViewModel model, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var ride = await FindAsync(model.RideId, token);
        var driverId = currentUser.UserId;
        if (driverId is null || ride.DriverId is not null ||
            ride.Status is not (RideStatus.Searching or RideStatus.Negotiating) ||
            model.CustomerPrice != ride.CustomerPrice)
            throw new ServiceException("ride_not_open", "لم يعد هذا الطلب متاحاً بالسعر المعروض.");
        var inviteData = RideSearchNotificationDispatcher.RideData(ride.Id);
        var profile = await db.DriverProfiles.AsNoTracking().Include(x => x.User)
            .SingleOrDefaultAsync(x => x.UserId == driverId && x.User.IsActive &&
                x.ServiceKindId == ride.ServiceKindId && x.ServiceCatalogItemId == ride.ServiceCatalogItemId, token);
        if (profile is null || !await db.Notifications.AsNoTracking().AnyAsync(
                x => x.UserId == driverId && x.Type == NotificationType.RideOffer && x.DataJson == inviteData, token))
            throw new ServiceException("ride_access_denied", "هذا الطلب غير موجه إلى حساب السائق.");
        var viewed = await db.RideSearchViews.SingleOrDefaultAsync(
            x => x.RideId == ride.Id && x.DriverId == driverId, token);
        var isNew = viewed is null;
        if (isNew)
        {
            viewed = new YemenDrive.Database.Entities.RideSearchView
            {
                RideId = ride.Id, DriverId = driverId.Value, ViewedAtUtc = DateTime.UtcNow
            };
            db.RideSearchViews.Add(viewed);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        var result = new RideSearchViewer(ride.Id, driverId.Value, profile.User.DisplayName ?? string.Empty,
            profile.PhotoUrl, viewed!.ViewedAtUtc);
        if (isNew && realtimePublisher is not null)
            await realtimePublisher.PublishRideRequestViewedAsync(ride.CustomerId, result, token);
        return result;
    }

    private async Task<YemenDrive.Database.Entities.Ride> FindAsync(int? rideId, CancellationToken token) =>
        await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rideId, token)
        ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
}
