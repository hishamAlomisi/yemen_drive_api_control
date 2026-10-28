using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record RideSearchRecipientModel(int? RideId = null);
public sealed record RideSearchRecipient(int RideId, int DriverId, string Name, string? PhotoUrl,
    DateTime SentAtUtc, DateTime? ViewedAtUtc);

public sealed class RideSearchRecipients(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<RideSearchRecipientModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get"];

    protected override async Task<object?> GetAsync(RideSearchRecipientModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(ride => ride.Id == model.RideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != userId && !await db.Users.AsNoTracking()
                .AnyAsync(user => user.Id == userId && user.Role == UserRole.Admin && user.IsActive, token))
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية عرض السائقين المرسل إليهم الطلب.");
        var invite = RideSearchNotificationDispatcher.RideData(ride.Id);
        // A nearby-driver snapshot is not proof of an invitation. Only rows saved
        // by the request dispatcher are eligible for this list; views are separate.
        var sent = await db.Notifications.AsNoTracking()
            .Where(notification => notification.Type == NotificationType.RideOffer && notification.DataJson == invite)
            .GroupBy(notification => notification.UserId)
            .Select(group => new { DriverId = group.Key, SentAtUtc = group.Min(notification => notification.CreatedAtUtc) })
            .ToListAsync(token);
        var ids = sent.Select(item => item.DriverId).ToArray();
        var drivers = await db.DriverProfiles.AsNoTracking().Where(profile => ids.Contains(profile.UserId))
            .Select(profile => new { profile.UserId, profile.User.DisplayName, profile.PhotoUrl })
            .ToDictionaryAsync(profile => profile.UserId, token);
        var views = await db.RideSearchViews.AsNoTracking().Where(view => view.RideId == ride.Id)
            .ToDictionaryAsync(view => view.DriverId, view => view.ViewedAtUtc, token);
        return sent.Where(item => drivers.ContainsKey(item.DriverId)).OrderBy(item => item.SentAtUtc)
            .Select(item => new RideSearchRecipient(ride.Id, item.DriverId,
                drivers[item.DriverId].DisplayName ?? string.Empty, drivers[item.DriverId].PhotoUrl,
                item.SentAtUtc, views.TryGetValue(item.DriverId, out var viewed) ? viewed : null)).ToArray();
    }
}

public sealed record RideRequestAvailabilityModel(int? RideId = null);

public sealed class RideRequestAvailability(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<RideRequestAvailabilityModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get"];

    protected override async Task<object?> GetAsync(RideRequestAvailabilityModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        if (model.RideId is not int rideId || rideId <= 0) return new { available = false };
        var invite = RideSearchNotificationDispatcher.RideData(rideId);
        var invited = await db.Notifications.AsNoTracking().AnyAsync(notification => notification.UserId == userId &&
            notification.Type == NotificationType.RideOffer && notification.DataJson == invite, token);
        return new { available = invited && (await RideRequestNotificationPolicy.EligibleRideIdsAsync(
            db, userId, [rideId], token)).Contains(rideId) };
    }
}
