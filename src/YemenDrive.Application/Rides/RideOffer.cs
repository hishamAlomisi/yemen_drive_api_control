using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideOfferEntity = YemenDrive.Database.Entities.RideOffer;

namespace YemenDrive.Application.Rides;

public sealed class RideOffer(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : OperationsService<RideOfferModel>(configurationStore)
{
    public override IReadOnlyCollection<string> Operations { get; } =
        ["add", "create", "update", "delete", "get", "accept"];

    protected override async Task<object?> AddAsync(RideOfferModel model, CancellationToken cancellationToken)
    {
        var authenticatedIsAdmin = currentUser.UserId is int authenticatedId &&
            await dbContext.Users.AsNoTracking().AnyAsync(
                x => x.Id == authenticatedId && x.Role == UserRole.Admin && x.IsActive,
                cancellationToken);
        var driverId = authenticatedIsAdmin
            ? model.DriverId ?? currentUser.UserId
            : currentUser.UserId ?? model.DriverId;
        if (model.RideId is null || driverId is null || model.Amount <= 0)
            throw new ServiceException("invalid_offer", "الرحلة والسائق والمبلغ مطلوبة.");
        if (currentUser.UserId is int authenticatedUserId &&
            model.DriverId is int requestedDriverId &&
            requestedDriverId != authenticatedUserId &&
            !authenticatedIsAdmin)
            throw new ServiceException("driver_access_denied", "لا يمكن إرسال عرض باسم سائق آخر.");
        var ride = await dbContext.Rides.SingleOrDefaultAsync(x => x.Id == model.RideId, cancellationToken)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.Status is not (RideStatus.Searching or RideStatus.Negotiating))
            throw new ServiceException("ride_not_open_for_offers", "الرحلة لا تستقبل عروضاً في حالتها الحالية.");
        if (!await dbContext.DriverProfiles.AnyAsync(
                x => x.UserId == driverId &&
                     x.ServiceKindId == ride.ServiceKindId &&
                     x.ServiceCatalogItemId == ride.ServiceCatalogItemId &&
                     x.IsAvailable &&
                     !dbContext.Rides.Any(activeRide =>
                         activeRide.DriverId == driverId && activeRide.Id != ride.Id &&
                         (activeRide.Status == RideStatus.DriverAssigned ||
                          activeRide.Status == RideStatus.DriverEnRoute ||
                          activeRide.Status == RideStatus.InProgress)),
                cancellationToken))
            throw new ServiceException("driver_not_available", "السائق غير متاح لإرسال عرض جديد حالياً.");

        var existing = await dbContext.RideOffers.SingleOrDefaultAsync(
            x => x.RideId == model.RideId && x.DriverId == driverId && x.Status == OfferStatus.Pending,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.ExpiresAtUtc <= DateTime.UtcNow)
            {
                existing.Status = OfferStatus.Expired;
            }
            else
            {
                existing.Amount = model.Amount;
                existing.Note = model.Note;
                existing.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(model.ValidForSeconds, 5, 300));
                existing.UpdatedAtUtc = DateTime.UtcNow;
                ride.Status = RideStatus.Negotiating;
                ride.UpdatedAtUtc = DateTime.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                return ToResult(existing);
            }
        }

        var entity = new RideOfferEntity
        {
            RideId = model.RideId.Value,
            DriverId = driverId.Value,
            Amount = model.Amount,
            Note = model.Note,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(model.ValidForSeconds, 5, 300))
        };
        ride.Status = RideStatus.Negotiating;
        ride.UpdatedAtUtc = DateTime.UtcNow;
        dbContext.RideOffers.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Notifications.Add(new Notification
        {
            UserId = ride.CustomerId,
            Type = NotificationType.RideOffer,
            Title = "عرض جديد من سائق",
            Body = "وصل عرض جديد لرحلتك. يمكنك مراجعته واختيار السائق المناسب.",
            DataJson = $"{{\"rideId\":{ride.Id},\"offerId\":{entity.Id}}}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(RideOfferModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        if (entity.Status != OfferStatus.Pending)
            throw new ServiceException("offer_not_pending", "لا يمكن تغيير عرض تمت معالجته مسبقاً.");
        if (entity.ExpiresAtUtc <= DateTime.UtcNow)
        {
            entity.Status = OfferStatus.Expired;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new ServiceException("offer_expired", "انتهت صلاحية العرض.");
        }

        var ride = await dbContext.Rides.Include(x => x.Offers)
            .SingleOrDefaultAsync(x => x.Id == entity.RideId, cancellationToken)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.Status is not (RideStatus.Searching or RideStatus.Negotiating))
            throw new ServiceException("ride_not_open_for_offers", "الرحلة لا يمكن قبول عرض لها في حالتها الحالية.");

        var requestedStatus = model.Status ?? OfferStatus.Accepted;
        if (requestedStatus == OfferStatus.Accepted)
            return await AcceptAsync(model, cancellationToken);

        entity.Status = requestedStatus;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new { offer = ToResult(entity), rideId = ride.Id, rideStatus = ride.Status, agreedPrice = ride.CustomerPrice, driverId = ride.DriverId };
    }

    protected override async Task<object?> AcceptAsync(RideOfferModel model, CancellationToken cancellationToken)
    {
        if (model.Id is null)
            throw new ServiceException("id_required", "معرف العرض مطلوب.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var entity = await dbContext.RideOffers
            .SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken)
            ?? throw new ServiceException("offer_not_found", "العرض غير موجود.");
        if (entity.Status != OfferStatus.Pending)
            throw new ServiceException("offer_not_pending", "لا يمكن قبول عرض تمت معالجته مسبقاً.");
        if (entity.ExpiresAtUtc <= DateTime.UtcNow)
        {
            entity.Status = OfferStatus.Expired;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new ServiceException("offer_expired", "انتهت صلاحية العرض.");
        }

        var ride = await dbContext.Rides.Include(x => x.Offers)
            .SingleOrDefaultAsync(x => x.Id == entity.RideId, cancellationToken)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        await EnsureCustomerOrAdminAsync(ride.CustomerId, cancellationToken);
        if (ride.Status is not (RideStatus.Searching or RideStatus.Negotiating))
            throw new ServiceException("ride_not_open_for_offers", "الرحلة لا يمكن قبول عرض لها في حالتها الحالية.");

        entity.Status = OfferStatus.Accepted;
        foreach (var other in ride.Offers.Where(x => x.Id != entity.Id && x.Status == OfferStatus.Pending))
            other.Status = OfferStatus.Rejected;

        ride.DriverId = entity.DriverId;
        ride.ServerPrice = entity.Amount;
        ride.CustomerPrice = entity.Amount;
        var pricingRule = await dbContext.PricingRules.AsNoTracking().SingleOrDefaultAsync(
            x => x.IsActive && x.ServiceKindId == ride.ServiceKindId &&
                 x.ServiceCatalogItemId == ride.ServiceCatalogItemId,
            cancellationToken);
        ride.ServiceFee = pricingRule?.ServiceFee ?? 0m;
        ride.CancellationFee = pricingRule?.CancellationFee ?? 0m;
        ride.TotalAmount = ride.CustomerPrice + ride.ServiceFee;
        var commissionRate = pricingRule?.DriverCommissionRate ?? 0m;
        var commissionFixed = pricingRule?.DriverCommissionFixed ?? 0m;
        ride.DriverCommissionAmount = Math.Round(
            ride.CustomerPrice.Value * commissionRate + commissionFixed,
            2,
            MidpointRounding.AwayFromZero);
        if (ride.DriverCommissionAmount > ride.CustomerPrice)
            throw new ServiceException("invalid_driver_commission", "عمولة السائق لا يمكن أن تتجاوز أجرة الرحلة.");
        // The service fee is paid by the customer to the platform. The driver
        // earns the agreed fare after their own commission is deducted.
        ride.DriverShare = ride.CustomerPrice - ride.DriverCommissionAmount;
        ride.PlatformShare = ride.ServiceFee + ride.DriverCommissionAmount;
        ride.Status = RideStatus.DriverAssigned;
        ride.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ServiceException("offer_concurrency_conflict", "تمت معالجة عرض آخر لهذه الرحلة. حدّث القائمة ثم حاول مجدداً.");
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ServiceException("offer_acceptance_conflict", "تعذر قبول العرض لأن حالة الرحلة تغيرت. حدّث القائمة ثم حاول مجدداً.");
        }

        return new
        {
            offer = ToResult(entity),
            rideId = ride.Id,
            driverId = ride.DriverId,
            agreedPrice = ride.CustomerPrice, serviceFee = ride.ServiceFee, totalAmount = ride.TotalAmount,
            rideStatus = ride.Status
        };
    }

    protected override async Task<object?> GetAsync(RideOfferModel model, CancellationToken cancellationToken) =>
        ToResult(await FindAsync(model.Id, cancellationToken, true));

    private async Task<RideOfferEntity> FindAsync(int? id, CancellationToken cancellationToken, bool noTracking = false)
    {
        if (id is null) throw new ServiceException("id_required", "معرف العرض مطلوب.");
        IQueryable<RideOfferEntity> query = dbContext.RideOffers;
        if (noTracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ServiceException("offer_not_found", "العرض غير موجود.");
    }

    private static object ToResult(RideOfferEntity x) => new
    {
        x.Id, x.RideId, x.DriverId, x.Amount, x.Status, x.ExpiresAtUtc, x.Note, x.CreatedAtUtc
    };

    private async Task EnsureCustomerOrAdminAsync(int customerId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not int userId)
            throw new ServiceException("authentication_required", "يجب تسجيل الدخول لقبول عرض السائق.");

        if (userId == customerId) return;
        if (await dbContext.Users.AsNoTracking().AnyAsync(
                x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive,
                cancellationToken)) return;

        throw new ServiceException("ride_access_denied", "لا تملك صلاحية قبول عرض هذه الرحلة.");
    }
}
