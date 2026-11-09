using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Security;
using YemenDrive.Application.Payments;
using YemenDrive.Shared.Api;

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
        if (model.IncludePaging && !isAdmin)
            throw new ServiceException("administrator_permission_required", "عرض صفحات سجل الرحلات متاح للإدارة فقط.");
        if ((model.IncludePaging && (model.PageNumber is < 1 or > 1_000_000 || model.PageSize is < 1 or > 100)) ||
            (isAdmin && model.Id <= 0))
            throw new ServiceException("invalid_ride_report_filter", "تحقق من رقم الرحلة وحجم الصفحة.");
        DriverProfile? driverProfile = null;
        if (!isAdmin && currentUserId is int userId)
        {
            driverProfile = await dbContext.DriverProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId && x.User.IsActive &&
                    x.User.Role == UserRole.Driver, cancellationToken);

            if (driverProfile is null)
            {
                query = query.Where(x => x.CustomerId == userId || x.DriverId == userId);
            }
            else
            {
                // A driver can see rides matching their assigned service while
                // they are still open for offers, in addition to their rides.
                // An assigned/en-route/in-progress ride makes the driver busy;
                // it must not be possible to accept a second customer at once.
                query = query.Where(x =>
                    x.DriverId == userId ||
                    (x.DriverId == null &&
                     (x.Status == RideStatus.Searching || x.Status == RideStatus.Negotiating) &&
                     x.ServiceKindId == driverProfile.ServiceKindId &&
                     x.ServiceCatalogItemId == driverProfile.ServiceCatalogItemId &&
                     !dbContext.Rides.Any(activeRide =>
                         activeRide.DriverId == userId && activeRide.ServiceEndedAtUtc == null &&
                         (activeRide.Status == RideStatus.DriverAssigned ||
                          activeRide.Status == RideStatus.DriverEnRoute ||
                          activeRide.Status == RideStatus.DriverArrived ||
                          activeRide.Status == RideStatus.InProgress || activeRide.Status == RideStatus.CancellationPending))));
            }
        }
        if (!isAdmin && currentUserId is null && model.CustomerId is not null)
            query = query.Where(x => x.CustomerId == model.CustomerId);
        if (model.ActiveOnly)
            query = query.Where(x => x.ServiceEndedAtUtc == null && (x.Status == RideStatus.DriverAssigned ||
                                     x.Status == RideStatus.DriverEnRoute ||
                                     x.Status == RideStatus.DriverArrived ||
                                     x.Status == RideStatus.InProgress ||
                                     x.Status == RideStatus.CancellationPending));
        if (model.Status is not null) query = query.Where(x => x.Status == model.Status);
        if (isAdmin && model.Id is not null) query = query.Where(x => x.Id == model.Id);
        var totalCount = model.IncludePaging ? await query.CountAsync(cancellationToken) : 0;
        var totalPages = model.IncludePaging ? Math.Max(1, (int)Math.Ceiling(totalCount / (double)model.PageSize)) : 1;
        var pageNumber = model.IncludePaging ? Math.Min(model.PageNumber, totalPages) : 1;
        var orderedQuery = query
            .Include(x => x.Customer)
            .Include(x => x.Driver)
            .Include(x => x.ServiceKind)
            .Include(x => x.ServiceCatalogItem)
            .Include(x => x.Offers)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id);
        var rides = await orderedQuery
            .Skip(model.IncludePaging ? (pageNumber - 1) * model.PageSize : 0)
            .Take(model.IncludePaging ? model.PageSize : 200)
            .ToListAsync(cancellationToken);

        var driverLocation = driverProfile is null
            ? null
            : await dbContext.DriverLiveLocations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.DriverId == driverProfile.UserId && x.IsOnline && x.ObservedAtUtc >= DateTime.UtcNow.AddMinutes(-5), cancellationToken);
        if (driverProfile is not null)
        {
            var now = DateTime.UtcNow;
            rides = rides.Where(x =>
                x.DriverId == driverProfile.UserId ||
                (driverProfile.IsAvailable && driverProfile.AcceptsNewRequests && driverProfile.IsApproved && driverProfile.ApprovedPassengerSeats >= (x.PassengerCount ?? (x.MoreThanFourPassengers ? 5 : 1)) && driverLocation?.IsOnline == true &&
                 (x.Status == RideStatus.Searching || x.Status == RideStatus.Negotiating) &&
                 RideDriverProximity.IsWithinRadius(x, driverLocation.Latitude, driverLocation.Longitude, now)))
                .ToList();
        }
        var rideIds = rides.Select(x => x.Id).ToArray();
        var movementSnapshots = await dbContext.RideMovementSnapshots.AsNoTracking()
            .Where(x => rideIds.Contains(x.RideId)).ToDictionaryAsync(x => x.RideId, cancellationToken);
        var finances = await RideFinancialState.ReadManyAsync(dbContext, rides, cancellationToken);
        var activeDriverIds = rides
            .Where(x => x.DriverId is not null &&
                        x.Status is RideStatus.DriverAssigned or RideStatus.DriverEnRoute or RideStatus.DriverArrived or
                            RideStatus.InProgress or RideStatus.CancellationPending)
            .Select(x => x.DriverId!.Value)
            .Distinct()
            .ToArray();
        var adminLiveLocations = driverProfile is null && activeDriverIds.Length > 0
            ? await dbContext.DriverLiveLocations.AsNoTracking()
                .Where(item => activeDriverIds.Contains(item.DriverId) && item.IsOnline &&
                               item.ObservedAtUtc >= DateTime.UtcNow.AddMinutes(-5))
                .ToDictionaryAsync(item => item.DriverId, cancellationToken)
            : new Dictionary<int, DriverLiveLocation>();
        var cashRequests = await dbContext.CashPaymentRequests.AsNoTracking()
            .Where(x => rideIds.Contains(x.RideId))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var paidMethods = await RidePaidMethod.LoadAsync(dbContext, rideIds, cancellationToken);
        var selectedMethods = await RideSelectedPaymentMethod.LoadAsync(dbContext, cancellationToken);
        var pendingWalletRideIds = await dbContext.PaymentWalletOperations.AsNoTracking()
            .Where(x => x.RideId != null && rideIds.Contains(x.RideId.Value) && x.IsOpen && x.Status != "Succeeded")
            .Select(x => x.RideId!.Value).Distinct().ToListAsync(cancellationToken);

        var rows = rides.Select(x =>
        {
            var assignedToCurrentDriver = driverProfile is not null && x.DriverId == driverProfile.UserId;
            // Eligibility, service, proximity and availability were checked above.
            // A driver needs the route to decide their offer; customer contact
            // details remain private until the ride is assigned to that driver.
            var revealContact = isAdmin || driverProfile is null || assignedToCurrentDriver;
            var revealDestination = revealContact || (driverProfile is not null &&
                x.DriverId is null && x.Status is RideStatus.Searching or RideStatus.Negotiating);
            var offer = driverProfile is null
                ? null
                : x.Offers.Where(item => item.DriverId == driverProfile.UserId)
                    .OrderByDescending(item => item.CreatedAtUtc).FirstOrDefault();
            var pickupName = RideLocationText.DisplayName(x.PickupLabel, x.PickupAddress, "نقطة الانطلاق");
            var destinationName = revealDestination
                ? RideLocationText.DisplayName(x.DestinationLabel, x.DestinationAddress, "الوجهة")
                : RideLocationText.AreaName(x.DestinationLabel, x.DestinationAddress, "ضمن المنطقة المحددة");
            var pickupDistanceMeters = driverLocation is null
                ? (double?)null
                : HaversineMeters(driverLocation.Latitude, driverLocation.Longitude, x.PickupLatitude, x.PickupLongitude);
            var cashRequest = cashRequests.FirstOrDefault(item => item.RideId == x.Id);
            paidMethods.TryGetValue(x.Id, out var paidMethod);
            adminLiveLocations.TryGetValue(x.DriverId ?? 0, out var adminLocation);
            movementSnapshots.TryGetValue(x.Id, out var movementSnapshot);

            return new
            {
                x.Id, x.CustomerId,
                customerName = revealContact ? x.Customer.DisplayName : null,
                customerPhone = revealContact ? x.Customer.PhoneNumber : null,
                x.DriverId,
                driverName = x.Driver?.DisplayName,
                x.Status, x.ServiceKindId, x.ServiceCatalogItemId,
                serviceKindCode = x.ServiceKind.Code, serviceKindNameAr = x.ServiceKind.NameAr,
                serviceCode = x.ServiceCatalogItem.Code, serviceNameAr = x.ServiceCatalogItem.NameAr,
                pickupDisplayName = pickupName,
                destinationDisplayName = destinationName,
                x.PickupLabel,
                x.PickupAddress,
                destinationLabel = revealDestination ? x.DestinationLabel : null,
                destinationAddress = revealDestination ? x.DestinationAddress : destinationName,
                x.MoreThanFourPassengers, x.PassengerCount, x.PassengerNotes,
                x.PickupLatitude, x.PickupLongitude,
                destinationLatitude = revealDestination ? x.DestinationLatitude : (double?)null,
                destinationLongitude = revealDestination ? x.DestinationLongitude : (double?)null,
                destinationAvailable = revealDestination,
                routePolyline = revealDestination ? x.RoutePolyline : null,
                tripDistanceMeters = revealDestination && x.RouteQuotedDistanceMeters > 0
                    ? x.RouteQuotedDistanceMeters : (double?)null,
                pickupDistanceMeters,
                driverLatitude = driverProfile is null ? adminLocation?.Latitude : driverLocation?.Latitude,
                driverLongitude = driverProfile is null ? adminLocation?.Longitude : driverLocation?.Longitude,
                driverBearing = driverProfile is null ? adminLocation?.Bearing : driverLocation?.Bearing,
                driverSpeed = driverProfile is null ? adminLocation?.Speed : driverLocation?.Speed,
                driverLocationObservedAtUtc = driverProfile is null ? adminLocation?.ObservedAtUtc : driverLocation?.ObservedAtUtc,
                x.CustomerPrice, x.ServiceFee, x.TotalAmount, x.DriverCommissionAmount, x.DriverShare, x.PlatformShare,
                createdAtUtc = MovementTimes.Utc(x.CreatedAtUtc), updatedAtUtc = MovementTimes.Utc(x.UpdatedAtUtc),
                acceptedAtUtc = MovementTimes.Utc(MovementTimes.Accepted(x, movementSnapshot)),
                completedAtUtc = MovementTimes.Utc(x.CompletedAtUtc),
                x.QuotedPrice, x.MinCustomerPrice, x.MaxCustomerPrice, x.WaitingFee,
                x.RouteVersion, x.RouteChangeFee, x.RouteChangeFees, x.RouteCompletedFare, x.RouteSegmentStartedAtUtc,
                x.WaitingFreeMinutes, x.WaitingPricePerMinute, x.WaitingMaxFee,
                x.CustomerPaymentEnabled, paymentCompleted = finances[x.Id].PaymentCompleted,
                finance = finances[x.Id],
                outstandingAmount = finances[x.Id].OutstandingAmount,
                paidAmount = finances[x.Id].PaidAmount,
                refundedAmount = finances[x.Id].RefundedAmount,
                serviceEndedAtUtc = MovementTimes.Utc(x.ServiceEndedAtUtc), passengerBoardedAtUtc = MovementTimes.Utc(x.PassengerBoardedAtUtc),
                paymentMethodNameAr = paidMethod?.PaymentMethodNameAr,
                selectedPaymentMethodNameAr = selectedMethods.GetValueOrDefault(x.PaymentMethodCode, "محفظة خارجية"),
                paymentProvider = paidMethod?.PaymentProvider,
                x.PaymentMethodCode,
                paymentRequiredBeforeStart = RidePaymentPolicy.RequiresBeforeStart(x),
                walletPaymentPending = pendingWalletRideIds.Contains(x.Id),
                driverOfferAmount = offer?.Amount,
                driverOfferExpiresAtUtc = offer?.ExpiresAtUtc,
                driverOfferStatus = offer?.Status,
                cashPaymentRequestId = cashRequest?.Id,
                cashPaymentRequestStatus = cashRequest?.Status
            };
        }).ToList();
        if (model.IncludePaging)
            return new { rows, totalCount, pageNumber, pageSize = model.PageSize, totalPages };
        return rows;
    }

    private static double HaversineMeters(double firstLatitude, double firstLongitude,
        double secondLatitude, double secondLongitude)
    {
        const double earthRadiusMeters = 6371000d;
        var latitudeDelta = DegreesToRadians(secondLatitude - firstLatitude);
        var longitudeDelta = DegreesToRadians(secondLongitude - firstLongitude);
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
                Math.Cos(DegreesToRadians(firstLatitude)) * Math.Cos(DegreesToRadians(secondLatitude)) *
                Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return 2 * earthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
