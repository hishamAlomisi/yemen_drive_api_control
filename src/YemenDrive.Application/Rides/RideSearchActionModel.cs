using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Application.Pricing;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record RideSearchActionModel(int? RideId = null, bool Exhausted = false, decimal? CustomerPrice = null, string? Reason = null);

public sealed class RideSearchAction(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser,
    IRideRealtimePublisher? realtimePublisher = null) : OperationsService<RideSearchActionModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["update", "cancel"];

    protected override async Task<object?> UpdateAsync(
        RideSearchActionModel model,
        CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, token);
        var ride = await FindRideAsync(model.RideId, token);
        await EnsureCustomerOrAdminAsync(ride.CustomerId, token);
        if (ride.DriverId is not null || ride.Status is not (
                RideStatus.Cancelled or RideStatus.Searching or RideStatus.Negotiating))
            throw new ServiceException(
                "ride_search_not_restartable",
                "لا يمكن إعادة البحث لأن الرحلة أُسندت إلى سائق أو أُغلقت نهائياً.");

        var now = DateTime.UtcNow;
        if (model.CustomerPrice is not null)
        {
            RidePricingPolicy.ValidateCustomerPrice(ride, model.CustomerPrice);
            if (model.CustomerPrice <= 0 || model.CustomerPrice != decimal.Truncate(model.CustomerPrice.Value))
                throw new ServiceException("invalid_customer_price", "أدخل أجرة صحيحة أكبر من صفر.");
            // Retrying a response lost in transit must not resend or expire offers twice.
            if (model.CustomerPrice == ride.CustomerPrice && ride.Status == RideStatus.Searching)
                return new { rideId = ride.Id, ride.Status, ride.CustomerPrice, notifiedDriverCount = 0 };
            ride.CustomerPrice = model.CustomerPrice;
        }
        var pendingOffers = await db.RideOffers
            .Where(x => x.RideId == ride.Id && x.Status == OfferStatus.Pending)
            .ToListAsync(token);
        foreach (var offer in pendingOffers)
        {
            offer.Status = OfferStatus.Expired;
            offer.UpdatedAtUtc = now;
        }

        // Reopen this ride and notify a fresh snapshot of drivers who are
        // currently online, available, nearby, and not assigned to a trip.
        ride.Status = RideStatus.Searching;
        ride.UpdatedAtUtc = now;
        await db.RideSearchViews.Where(x => x.RideId == ride.Id).ExecuteDeleteAsync(token);
        var recipients = await RideSearchNotificationDispatcher
            .NotifyAvailableDriversAsync(db, ride, token, priceChanged: model.CustomerPrice is not null);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher is not null)
        {
            await realtimePublisher.PublishRideAvailableAsync(recipients, token);
            var expiredOffers = await RideRealtimeEventFactory.LoadOffersAsync(
                db, pendingOffers.Select(offer => offer.Id), token);
            await realtimePublisher.PublishOfferUpdatesAsync(
                ride.CustomerId, expiredOffers, token);
            await realtimePublisher.PublishRideStatusUpdatedAsync(
                RideRealtimeEventFactory.Status(ride), token);
        }
        return new { rideId = ride.Id, ride.Status, ride.CustomerPrice, notifiedDriverCount = recipients.Count };
    }

    protected override async Task<object?> CancelAsync(
        RideSearchActionModel model,
        CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, token);
        var ride = await FindRideAsync(model.RideId, token);
        await EnsureCustomerOrAdminAsync(ride.CustomerId, token);
        if (ride.DriverId is not null)
            throw new ServiceException(
                "assigned_ride_cancellation_required",
                "الرحلة مرتبطة بسائق؛ استخدم طلب الإلغاء الرسمي.");
        if (!string.IsNullOrWhiteSpace(model.Reason))
        {
            var reason = model.Reason.Trim();
            if (reason.Length is < 3 or > 1000)
                throw new ServiceException("invalid_cancellation_reason", "حدد سبب الإلغاء بين 3 و1000 حرف.");
            if (!await db.RideCancellationRequests.AnyAsync(x => x.RideId == ride.Id, token))
                db.RideCancellationRequests.Add(new YemenDrive.Database.Entities.RideCancellationRequest
                {
                    RideId = ride.Id, CustomerId = ride.CustomerId,
                    RideStatusAtRequest = ride.Status, Reason = reason,
                    Status = RideCancellationStatus.AdminApproved
                });
        }
        if (ride.Status == RideStatus.Cancelled)
        {
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new { rideId = ride.Id, ride.Status };
        }
        if (ride.Status is not (RideStatus.Searching or RideStatus.Negotiating))
            throw new ServiceException(
                "ride_search_not_open",
                "لا يوجد بحث مفتوح يمكن إيقافه لهذه الرحلة.");

        var now = DateTime.UtcNow;
        var pendingOffers = await db.RideOffers
            .Where(x => x.RideId == ride.Id && x.Status == OfferStatus.Pending)
            .ToListAsync(token);
        foreach (var offer in pendingOffers)
        {
            offer.Status = OfferStatus.Expired;
            offer.UpdatedAtUtc = now;
        }

        ride.Status = RideStatus.Cancelled;
        ride.UpdatedAtUtc = now;

        var inviteData = RideSearchNotificationDispatcher.RideData(ride.Id);
        var invitedDriverIds = await db.Notifications.AsNoTracking()
            .Where(x => x.Type == NotificationType.RideOffer && x.DataJson == inviteData)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(token);
        var recipientIds = invitedDriverIds
            .Concat(pendingOffers.Select(x => x.DriverId))
            .Distinct()
            .ToArray();
        var isExhausted = model.Exhausted;
        var noticeTitle = isExhausted ? "انتهى البحث عن سائق" : "ألغى العميل البحث";
        var noticeBody = isExhausted
            ? "انتهى البحث عن سائق لهذه الرحلة وأُغلق الطلب."
            : "ألغى العميل البحث عن هذه الرحلة؛ لا يمكنك إرسال عرض عليها.";
        db.Notifications.AddRange(recipientIds.Select(driverId => new Notification
        {
            UserId = driverId,
            Type = NotificationType.RideStatus,
            Title = noticeTitle,
            Body = noticeBody,
            DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{ride.Id},\"status\":\"Cancelled\"}}"
        }));

        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher is not null)
        {
            await realtimePublisher.PublishRideUnavailableAsync(
                ride.Id, ride.CustomerId, recipientIds, token);
            var expiredOffers = await RideRealtimeEventFactory.LoadOffersAsync(
                db, pendingOffers.Select(offer => offer.Id), token);
            await realtimePublisher.PublishOfferUpdatesAsync(
                ride.CustomerId, expiredOffers, token);
            await realtimePublisher.PublishRideStatusUpdatedAsync(
                RideRealtimeEventFactory.Status(ride), token);
        }
        return new { rideId = ride.Id, ride.Status };
    }

    private async Task<YemenDrive.Database.Entities.Ride> FindRideAsync(
        int? rideId,
        CancellationToken token)
    {
        if (rideId is null)
            throw new ServiceException("ride_id_required", "رقم الرحلة مطلوب.");
        return await db.Rides.SingleOrDefaultAsync(x => x.Id == rideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
    }

    private async Task EnsureCustomerOrAdminAsync(int customerId, CancellationToken token)
    {
        if (currentUser.UserId is not int userId)
            throw new ServiceException("authentication_required", "يجب تسجيل الدخول لتنفيذ العملية.");
        if (userId == customerId) return;
        if (await db.Users.AsNoTracking().AnyAsync(
                x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, token)) return;
        throw new ServiceException("ride_access_denied", "لا تملك صلاحية إدارة البحث عن هذه الرحلة.");
    }
}
