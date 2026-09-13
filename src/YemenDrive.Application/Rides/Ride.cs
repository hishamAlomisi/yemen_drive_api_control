using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public sealed class Ride(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : OperationsService<RideModel>(configurationStore)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["add", "create", "update", "delete", "get", "cancel"];
    protected override async Task<object?> AddAsync(RideModel model, CancellationToken cancellationToken)
    {
        // A signed-in client is always bound to the token. The explicit id is
        // retained only for unauthenticated administrative/test requests.
        var customerId = currentUser.UserId ?? model.CustomerId;
        if (customerId is null || model.ServiceKindId is null || model.ServiceCatalogItemId is null ||
            string.IsNullOrWhiteSpace(model.PickupAddress) ||
            string.IsNullOrWhiteSpace(model.DestinationAddress))
            throw new ServiceException("invalid_ride", "الخدمة والمركبة وموقعا الانطلاق والوصول مطلوبة، ويجب تسجيل الدخول.");

        if (!await dbContext.Users.AnyAsync(x => x.Id == customerId, cancellationToken))
            throw new ServiceException("customer_not_found", "العميل غير موجود.");

        var idempotencyKey = string.IsNullOrWhiteSpace(model.IdempotencyKey)
            ? null
            : model.IdempotencyKey.Trim();
        if (idempotencyKey?.Length > 128)
            throw new ServiceException("invalid_idempotency_key", "مفتاح منع التكرار أطول من المسموح.");
        if (idempotencyKey is not null)
        {
            var existing = await dbContext.Rides.AsNoTracking().Include(x => x.ServiceKind)
                .Include(x => x.ServiceCatalogItem)
                .SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null) return ToResult(existing);
        }

        var selectedService = await dbContext.ServiceCatalogItems.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == model.ServiceCatalogItemId && x.ServiceKindId == model.ServiceKindId && x.IsActive, cancellationToken)
            ?? throw new ServiceException("service_not_found", "الخدمة المختارة غير موجودة أو غير متاحة لهذا النوع.");

        var entity = new RideEntity
        {
            CustomerId = customerId.Value,
            ServiceKindId = model.ServiceKindId.Value,
            ServiceCatalogItemId = selectedService.Id,
            PickupLabel = model.PickupLabel?.Trim() ?? string.Empty,
            PickupAddress = model.PickupAddress.Trim(),
            PickupLatitude = model.PickupLatitude,
            PickupLongitude = model.PickupLongitude,
            DestinationLabel = model.DestinationLabel?.Trim() ?? string.Empty,
            DestinationAddress = model.DestinationAddress.Trim(),
            DestinationLatitude = model.DestinationLatitude,
            DestinationLongitude = model.DestinationLongitude,
            CustomerPrice = model.CustomerPrice,
            IdempotencyKey = idempotencyKey,
            Status = model.Status ?? RideStatus.Searching
        };
        dbContext.Rides.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        var driverIds = await dbContext.DriverProfiles.AsNoTracking()
            .Where(x => x.IsAvailable && x.ServiceKindId == entity.ServiceKindId &&
                        x.ServiceCatalogItemId == entity.ServiceCatalogItemId)
            .Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        foreach (var driverId in driverIds)
        {
            dbContext.Notifications.Add(new Notification
            {
                UserId = driverId,
                Type = NotificationType.RideOffer,
                Title = "طلب رحلة جديد",
                Body = $"يوجد طلب جديد من العميل في {entity.PickupAddress} إلى {entity.DestinationAddress}.",
                DataJson = $"{{\"rideId\":{entity.Id}}}"
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(RideModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        if (model.DriverId is not null)
        {
            var hasAcceptedOffer = await dbContext.RideOffers.AnyAsync(
                x => x.RideId == entity.Id && x.DriverId == model.DriverId && x.Status == OfferStatus.Accepted,
                cancellationToken);
            if (!hasAcceptedOffer)
                throw new ServiceException("accepted_offer_required", "لا يمكن تعيين السائق قبل قبول عرضه.");
            entity.DriverId = model.DriverId;
        }
        if (model.Status is not null)
        {
            ValidateStatusTransition(entity.Status, model.Status.Value);
            if (model.Status.Value is RideStatus.DriverEnRoute or RideStatus.InProgress or RideStatus.Completed &&
                (currentUser.UserId != entity.DriverId && !await IsAdminAsync(cancellationToken)))
                throw new ServiceException("driver_action_required", "تغيير حالة الرحلة التشغيلية متاح للسائق المسند فقط.");
            entity.Status = model.Status.Value;
        }
        if (model.CustomerPrice is not null)
        {
            if (entity.Status is not (RideStatus.Searching or RideStatus.Negotiating))
                throw new ServiceException("price_already_agreed", "لا يمكن تغيير السعر بعد قبول عرض السائق.");
            entity.CustomerPrice = model.CustomerPrice;
        }
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (entity.Status == RideStatus.InProgress && entity.StartedAtUtc is null) entity.StartedAtUtc = DateTime.UtcNow;
        if (entity.Status == RideStatus.Completed && entity.CompletedAtUtc is null) entity.CompletedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await NotifyOtherParticipantAsync(entity, "تحديث حالة الرحلة", StatusMessage(entity.Status), cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> DeleteAsync(RideModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        dbContext.Rides.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new { entity.Id };
    }

    protected override async Task<object?> CancelAsync(RideModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        if (entity.Status is RideStatus.Completed or RideStatus.Cancelled)
            throw new ServiceException("ride_not_cancellable", "لا يمكن إلغاء الرحلة في حالتها الحالية.");
        entity.Status = RideStatus.Cancelled;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await NotifyOtherParticipantAsync(entity, "تم إلغاء الرحلة", "تم إلغاء الرحلة من الطرف الآخر.", cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> GetAsync(RideModel model, CancellationToken cancellationToken)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف الرحلة مطلوب.");
        var entity = await dbContext.Rides.AsNoTracking().Include(x => x.Offers)
            .ThenInclude(x => x.Driver!).ThenInclude(x => x.DriverProfile)
            .Include(x => x.Driver!).ThenInclude(x => x.DriverProfile)
            .Include(x => x.ServiceKind).Include(x => x.ServiceCatalogItem)
            .SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        EnsureRideAccess(entity);
        var payments = await dbContext.PaymentTransactions.AsNoTracking()
            .Where(x => x.RideId == entity.Id)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new { x.Id, x.Amount, x.Currency, x.Provider, x.Status, x.ProviderReference, x.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        var driverLocation = entity.DriverId is null
            ? null
            : await dbContext.DriverLiveLocations.AsNoTracking()
                .Where(x => x.DriverId == entity.DriverId)
                .Select(x => new { x.DriverId, x.Latitude, x.Longitude, x.Bearing, x.Speed, x.IsOnline, x.ObservedAtUtc })
                .SingleOrDefaultAsync(cancellationToken);
        return new
        {
            ride = ToResult(entity),
            driver = entity.Driver is null ? null : new
            {
                entity.Driver.Id,
                name = entity.Driver.DisplayName,
                phoneNumber = entity.Driver.PhoneNumber,
                vehicleModel = entity.Driver.DriverProfile == null ? null : entity.Driver.DriverProfile.VehicleModel,
                plateNumber = entity.Driver.DriverProfile == null ? null : entity.Driver.DriverProfile.PlateNumber,
                rating = entity.Driver.DriverProfile == null ? null : (decimal?)entity.Driver.DriverProfile.Rating,
                photoUrl = entity.Driver.DriverProfile == null ? null : entity.Driver.DriverProfile.PhotoUrl
            },
            driverLocation,
            offers = entity.Offers.Select(x => new
            {
                x.Id,
                x.DriverId,
                driverName = x.Driver?.DisplayName,
                vehicleModel = x.Driver?.DriverProfile?.VehicleModel,
                photoUrl = x.Driver?.DriverProfile?.PhotoUrl,
                x.Amount,
                x.Status,
                x.ExpiresAtUtc,
                x.CreatedAtUtc,
                x.Note
            }),
            payments
        };
    }

    private async Task<RideEntity> FindAsync(int? id, CancellationToken cancellationToken)
    {
        if (id is null) throw new ServiceException("id_required", "معرف الرحلة مطلوب.");
        var entity = await dbContext.Rides.Include(x => x.ServiceKind).Include(x => x.ServiceCatalogItem)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        EnsureRideAccess(entity);
        return entity;
    }

    private void EnsureRideAccess(RideEntity ride)
    {
        if (currentUser.UserId is int userId && ride.CustomerId != userId && ride.DriverId != userId)
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية الوصول إلى هذه الرحلة.");
    }

    private Task<bool> IsAdminAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is not int userId
            ? Task.FromResult(false)
            : dbContext.Users.AsNoTracking().AnyAsync(
                x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive,
            cancellationToken);

    private async Task NotifyOtherParticipantAsync(
        RideEntity ride,
        string title,
        string body,
        CancellationToken cancellationToken)
    {
        if (ride.DriverId is null || currentUser.UserId is not int actorId)
            return;
        var recipientId = actorId == ride.CustomerId ? ride.DriverId.Value : ride.CustomerId;
        if (recipientId == actorId) return;
        dbContext.Notifications.Add(new Notification
        {
            UserId = recipientId,
            Type = NotificationType.RideStatus,
            Title = title,
            Body = body,
            DataJson = $"{{\"rideId\":{ride.Id},\"status\":\"{ride.Status}\"}}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string StatusMessage(RideStatus status) => status switch
    {
        RideStatus.DriverEnRoute => "السائق في طريقه إليك.",
        RideStatus.InProgress => "بدأت الرحلة الآن.",
        RideStatus.Completed => "وصلت الرحلة إلى مرحلة الدفع.",
        RideStatus.DriverAssigned => "تم تعيين سائق للرحلة.",
        _ => "تم تحديث حالة الرحلة."
    };

    private static object ToResult(RideEntity x) => new
    {
        x.Id, x.CustomerId, x.DriverId, x.Status, x.ServiceKindId, x.ServiceCatalogItemId,
        serviceKindCode = x.ServiceKind?.Code, serviceKindNameAr = x.ServiceKind?.NameAr,
        serviceCode = x.ServiceCatalogItem?.Code, serviceNameAr = x.ServiceCatalogItem?.NameAr,
        x.PickupLabel, x.PickupAddress, x.PickupLatitude, x.PickupLongitude,
        x.DestinationLabel, x.DestinationAddress, x.DestinationLatitude, x.DestinationLongitude,
        x.ServerPrice, x.CustomerPrice, x.DriverShare, x.PlatformShare,
        x.StartedAtUtc, x.CompletedAtUtc, x.CreatedAtUtc, x.UpdatedAtUtc
    };

    private static void ValidateStatusTransition(RideStatus current, RideStatus next)
    {
        if (current == next) return;
        var valid = current switch
        {
            RideStatus.Draft => next is RideStatus.Searching or RideStatus.Cancelled,
            RideStatus.Searching => next is RideStatus.Negotiating or RideStatus.DriverAssigned or RideStatus.Cancelled,
            RideStatus.Negotiating => next is RideStatus.DriverAssigned or RideStatus.Cancelled,
            RideStatus.DriverAssigned => next is RideStatus.DriverEnRoute or RideStatus.Cancelled,
            RideStatus.DriverEnRoute => next is RideStatus.InProgress or RideStatus.Cancelled,
            RideStatus.InProgress => next is RideStatus.Completed or RideStatus.Cancelled,
            RideStatus.Completed or RideStatus.Cancelled => false,
            _ => false
        };
        if (!valid)
            throw new ServiceException("invalid_status_transition", $"لا يمكن تغيير حالة الرحلة من {current} إلى {next}.");
    }
}
