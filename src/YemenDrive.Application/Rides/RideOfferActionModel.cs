using Microsoft.EntityFrameworkCore;
using System.Data;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

// Compatibility contract used by the Flutter negotiation client. The main
// RideOfferModel remains the administration/general-purpose contract.
public sealed record RideOfferActionModel(int? RideId = null, int? OfferId = null);

public sealed class RideOfferAction(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<RideOfferActionModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["accept", "reject"];

    protected override async Task<object?> AcceptAsync(
        RideOfferActionModel model, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var offer = await FindOfferAsync(model, token);
        var ride = await db.Rides.Include(x => x.Offers)
            .SingleOrDefaultAsync(x => x.Id == offer.RideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        await EnsureCustomerOrAdminAsync(ride.CustomerId, token);
        if (offer.Status != OfferStatus.Pending)
            throw new ServiceException("offer_not_pending", "لا يمكن قبول عرض تمت معالجته مسبقاً.");
        if (offer.ExpiresAtUtc <= DateTime.UtcNow)
            throw new ServiceException("offer_expired", "انتهت صلاحية العرض.");

        offer.Status = OfferStatus.Accepted;
        foreach (var other in ride.Offers.Where(x => x.Id != offer.Id && x.Status == OfferStatus.Pending))
            other.Status = OfferStatus.Rejected;
        ride.DriverId = offer.DriverId;
        ride.ServerPrice = offer.Amount;
        ride.CustomerPrice = offer.Amount;
        ride.Status = RideStatus.DriverAssigned;
        ride.UpdatedAtUtc = DateTime.UtcNow;
        offer.UpdatedAtUtc = DateTime.UtcNow;
        db.Notifications.Add(new Notification
        {
            UserId = offer.DriverId,
            Type = NotificationType.RideStatus,
            Title = "تم قبول عرضك",
            Body = "تم تعيينك لهذه الرحلة. يمكنك التوجه إلى العميل.",
            DataJson = $"{{\"rideId\":{ride.Id},\"status\":\"DriverAssigned\"}}"
        });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new { offerId = offer.Id, rideId = ride.Id, driverId = ride.DriverId, agreedPrice = ride.CustomerPrice, rideStatus = ride.Status };
    }

    protected override async Task<object?> UpdateAsync(RideOfferActionModel model, CancellationToken token) =>
        await RejectAsync(model, token);

    protected override async Task<object?> RejectAsync(RideOfferActionModel model, CancellationToken token)
    {
        var offer = await FindOfferAsync(model, token);
        var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == offer.RideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        await EnsureCustomerOrAdminAsync(ride.CustomerId, token);
        if (offer.Status != OfferStatus.Pending)
            throw new ServiceException("offer_not_pending", "لا يمكن رفض عرض تمت معالجته مسبقاً.");
        offer.Status = OfferStatus.Rejected;
        offer.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        return new { offerId = offer.Id, rideId = ride.Id, status = offer.Status };
    }

    private async Task<YemenDrive.Database.Entities.RideOffer> FindOfferAsync(
        RideOfferActionModel model, CancellationToken token)
    {
        if (model.OfferId is null)
            throw new ServiceException("offer_id_required", "معرف العرض مطلوب.");
        var offer = await db.RideOffers.SingleOrDefaultAsync(x => x.Id == model.OfferId, token)
            ?? throw new ServiceException("offer_not_found", "العرض غير موجود.");
        if (model.RideId is not null && offer.RideId != model.RideId)
            throw new ServiceException("offer_not_found", "العرض لا يتبع الرحلة المحددة.");
        return offer;
    }

    private async Task EnsureCustomerOrAdminAsync(int customerId, CancellationToken token)
    {
        if (currentUser.UserId is not int userId)
            throw new ServiceException("authentication_required", "يجب تسجيل الدخول لتنفيذ العملية.");
        if (userId == customerId) return;
        if (await db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, token)) return;
        throw new ServiceException("ride_access_denied", "لا تملك صلاحية تنفيذ العملية على هذه الرحلة.");
    }
}
