using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Application.Maps;
using YemenDrive.Application.Pricing;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public sealed record RideRouteChangeModel(int? RideId = null, int? RequestId = null, Guid? RequestKey = null,
    double? NewDestinationLatitude = null, double? NewDestinationLongitude = null, string? NewDestinationLabel = null);

public sealed class RideRouteChangeService(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user, GoogleMapsGateway maps, IRideRealtimePublisher? publisher = null) : ModelService<RideRouteChangeModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "quote", "add", "accept", "reject", "cancel"];

    protected override async Task<object?> ExecuteOperationAsync(string operation, RideRouteChangeModel model, CancellationToken ct)
    {
        operation = operation.ToLowerInvariant();
        if (!Operations.Contains(operation)) throw new ServiceException("operation_not_supported", "عملية تغيير المسار غير صحيحة.");
        var userId = user.UserId ?? throw new ServiceException("authentication_required", "سجّل الدخول لإدارة مسار الرحلة.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (model.RideId is int rideId) await RidePaymentGuard.LockAsync(db, rideId, ct);
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.Id == model.RideId, ct)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        var admin = operation == "get" && await db.Users.AnyAsync(x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, ct);
        if (userId != ride.CustomerId && userId != ride.DriverId && !admin)
            throw new ServiceException("ride_access_denied", "هذه الرحلة لا تخصك.");
        if (operation is "quote" or "add" or "cancel" && userId != ride.CustomerId)
            throw new ServiceException("route_customer_required", "طلب تغيير الوجهة ومراجعة الفاتورة متاحان للراكب فقط.");
        if (operation is "accept" or "reject" && userId != ride.DriverId)
            throw new ServiceException("route_driver_required", "الموافقة على تغيير الوجهة أو رفضه متاحان للسائق المسند فقط.");
        var now = DateTime.UtcNow;
        var open = await db.RideRouteChanges.SingleOrDefaultAsync(x => x.RideId == ride.Id && x.IsOpen, ct);
        var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, ct);
        if (open != null && (open.ExpiresAtUtc <= now || ride.Status != RideStatus.InProgress || ride.CustomerPaymentEnabled ||
            open.RouteVersion != ride.RouteVersion || open.PreviousTotal != ride.TotalAmount ||
            (location != null && RideWaitingCoordinator.DistanceMeters(open.ChangeLatitude, open.ChangeLongitude, location.Latitude, location.Longitude) > 80)))
        {
            var notified = open.Status == "pending";
            Finish(open, "expired", now);
            if (notified) Notify(ride, open.Id, "انتهى عرض تغيير المسار", "بقي المسار الحالي. اطلب عرضًا جديدًا لمراجعة السعر والموقع الحاليين.");
            await db.SaveChangesAsync(ct); // release the filtered index before another quote
        }

        RideRouteChange? requested = null;
        if (operation == "quote")
        {
            if (model.RequestKey is null || model.RequestKey == Guid.Empty ||
                model.NewDestinationLatitude is not { } latitude || model.NewDestinationLongitude is not { } longitude ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
                string.IsNullOrWhiteSpace(model.NewDestinationLabel) || model.NewDestinationLabel.Length > 500)
                throw new ServiceException("invalid_route_destination", "حدد وجهة صحيحة واسم المكان ومعرف الطلب.");
            requested = await db.RideRouteChanges.SingleOrDefaultAsync(x => x.RideId == ride.Id && x.RequestKey == model.RequestKey, ct);
            if (requested != null)
            {
                if (requested.RequestedByUserId != userId || requested.NewDestinationLatitude != latitude || requested.NewDestinationLongitude != longitude)
                    throw new ServiceException("route_request_key_mismatch", "معرف الطلب مستخدم لوجهة أخرى.");
            }
            else
            {
                await EnsureCanChangeAsync(ride, ct);
                if (open?.IsOpen == true) throw new ServiceException("route_change_pending", "يوجد عرض تغيير مسار مفتوح؛ أكمله أو ألغِه أولًا.");
                if (RideWaitingCoordinator.DistanceMeters(ride.DestinationLatitude, ride.DestinationLongitude, latitude, longitude) < 25)
                    throw new ServiceException("route_destination_unchanged", "الوجهة الجديدة مطابقة للوجهة الحالية.");
                location = await RideRouteChangePolicy.RequireLocationAsync(db, ride, ct);
                var travelled = await RideRouteChangePolicy.MeasureAsync(db, ride, location, ct);
                var newRoute = await maps.ComputeRouteAsync(location.Latitude, location.Longitude, latitude, longitude, false, ct);
                if (newRoute.DistanceMeters <= 0 || newRoute.DurationSeconds <= 0)
                    throw new ServiceException("route_not_found", "لم يصل مسار صالح للوجهة الجديدة.");
                var rawOriginal = ride.RouteBaseFare + ride.RoutePerKilometer * (decimal)(ride.RouteQuotedDistanceMeters / 1000) +
                    ride.RoutePerMinute * (decimal)(ride.RouteQuotedDurationSeconds / 60);
                if (rawOriginal <= 0 || ride.RouteOriginalFare <= 0)
                    throw new ServiceException("route_pricing_unavailable", "لا توجد تسعيرة أصلية موثقة لتوزيع أجرة هذه الرحلة.");
                var factor = ride.RouteOriginalFare / rawOriginal;
                var segmentFare = RidePricingPolicy.Round(((ride.RouteVersion == 0 ? ride.RouteBaseFare : 0) +
                    ride.RoutePerKilometer * (decimal)(travelled.Meters / 1000) + ride.RoutePerMinute * (decimal)(travelled.Seconds / 60)) * factor);
                requested = new RideRouteChange
                {
                    RideId = ride.Id, RequestKey = model.RequestKey.Value, RequestedByUserId = userId, RouteVersion = ride.RouteVersion,
                    CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(2), ChangeObservedAtUtc = location.ObservedAtUtc,
                    ChangeLatitude = location.Latitude, ChangeLongitude = location.Longitude,
                    OldDestinationLabel = RideLocationText.DisplayName(ride.DestinationLabel, ride.DestinationAddress, "الوجهة السابقة"),
                    OldDestinationLatitude = ride.DestinationLatitude, OldDestinationLongitude = ride.DestinationLongitude,
                    NewDestinationLabel = model.NewDestinationLabel.Trim(), NewDestinationLatitude = latitude, NewDestinationLongitude = longitude,
                    NewRoutePolyline = newRoute.EncodedPolyline, PreviousTotal = ride.TotalAmount ?? 0,
                    CompletedDistanceMeters = ride.RouteCompletedDistanceMeters + travelled.Meters, CompletedDurationSeconds = travelled.Seconds,
                    CompletedFare = ride.RouteCompletedFare + segmentFare, OriginalServiceFee = ride.ServiceFee,
                    RouteChangeFee = ride.RouteChangeFee, PreviousRouteChangeFees = ride.RouteChangeFees,
                    NewDistanceMeters = newRoute.DistanceMeters, NewDurationSeconds = newRoute.DurationSeconds,
                    NewRouteFare = RidePricingPolicy.Round((ride.RoutePerKilometer * (decimal)(newRoute.DistanceMeters / 1000d) +
                        ride.RoutePerMinute * (decimal)(newRoute.DurationSeconds / 60d)) * factor), WaitingFee = ride.WaitingFee
                };
                requested.NewTotal = requested.CompletedFare + requested.NewRouteFare + requested.RouteChangeFee +
                    requested.PreviousRouteChangeFees + requested.OriginalServiceFee + requested.WaitingFee;
                if (requested.NewTotal <= 0 || requested.NewTotal > 999999999)
                    throw new ServiceException("invalid_fare", "أجرة المسار الجديد خارج النطاق المسموح.");
                db.RideRouteChanges.Add(requested);
            }
        }
        else if (operation != "get")
        {
            requested = await db.RideRouteChanges.SingleOrDefaultAsync(x => x.Id == model.RequestId && x.RideId == ride.Id, ct)
                ?? throw new ServiceException("route_request_not_found", "طلب تغيير المسار غير موجود.");
            var mine = requested.RequestedByUserId == userId;
            if ((operation is "add" or "cancel") != mine)
                throw new ServiceException("route_other_party_required", "يجب أن يعتمد التغيير الطرف الآخر في الرحلة.");
            if (requested.Status == "expired")
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                throw new ServiceException("route_quote_expired", "انتهى العرض أو تغير موقع المركبة. اطلب تسعيرة جديدة وراجع الفاتورة.");
            }
            if (requested.IsOpen)
            {
                if (operation is "add" or "accept")
                {
                    await EnsureCanChangeAsync(ride, ct);
                    await RideRouteChangePolicy.RequireLocationAsync(db, ride, ct);
                }
                if (operation == "add" && requested.Status == "quoted")
                {
                    requested.Status = "pending";
                    Notify(ride, requested.Id, "طلب تغيير مسار الرحلة", $"الوجهة: {requested.NewDestinationLabel}. المنفذ {requested.CompletedFare:0} + رسوم الرحلة {requested.OriginalServiceFee:0} + رسوم التغيير {requested.RouteChangeFee:0} + المسار الجديد {requested.NewRouteFare:0} ر.ي. الإجمالي {requested.NewTotal:0} ر.ي. راجع الفاتورة للموافقة أو الرفض.");
                }
                else if (operation == "accept")
                {
                    if (requested.Status != "pending") throw new ServiceException("route_not_confirmed", "لم يؤكد صاحب الطلب الفاتورة بعد.");
                    Finish(requested, "accepted", now);
                    requested.ApprovedByUserId = userId;
                    ride.DestinationLatitude = requested.NewDestinationLatitude;
                    ride.DestinationLongitude = requested.NewDestinationLongitude;
                    ride.DestinationLabel = requested.NewDestinationLabel;
                    ride.DestinationAddress = requested.NewDestinationLabel;
                    ride.RoutePolyline = requested.NewRoutePolyline;
                    ride.RouteCompletedFare = requested.CompletedFare;
                    ride.RouteCompletedDistanceMeters = requested.CompletedDistanceMeters;
                    ride.RouteChangeFees = requested.PreviousRouteChangeFees + requested.RouteChangeFee;
                    ride.RouteSegmentStartedAtUtc = requested.ChangeObservedAtUtc;
                    ride.RouteSegmentLatitude = requested.ChangeLatitude;
                    ride.RouteSegmentLongitude = requested.ChangeLongitude;
                    ride.RouteVersion++;
                    ride.CustomerPrice = requested.CompletedFare + requested.NewRouteFare + ride.RouteChangeFees;
                    ride.ServerPrice = ride.CustomerPrice;
                    RidePricingPolicy.UpdateTotals(ride);
                    Notify(ride, requested.Id, "تم اعتماد المسار الجديد", $"الوجهة: {requested.NewDestinationLabel}. الأجرة الجديدة {ride.TotalAmount:0} ر.ي شاملة الرسوم المعتمدة.");
                }
                else if (operation == "reject" || operation == "cancel")
                {
                    if (operation == "reject" && requested.Status != "pending") throw new ServiceException("route_not_confirmed", "الطلب لم يُرسل بعد.");
                    var wasSent = requested.Status == "pending";
                    Finish(requested, operation == "reject" ? "rejected" : "cancelled", now);
                    if (wasSent) Notify(ride, requested.Id, "بقي مسار الرحلة الحالي", "لم يُعتمد طلب تغيير المسار ولم تُضف رسوم تغيير.");
                }
            }
        }
        var changed = db.ChangeTracker.HasChanges();
        if (changed) ride.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        var result = await SnapshotAsync(ride, userId, admin, requested, ct);
        await transaction.CommitAsync(ct);
        if (changed && publisher != null) await publisher.PublishRideStatusUpdatedAsync(RideRealtimeEventFactory.Status(ride), ct);
        return result;
    }

    private async Task EnsureCanChangeAsync(RideEntity ride, CancellationToken ct)
    {
        if (ride.Status != RideStatus.InProgress || ride.DriverId == null || ride.CustomerPaymentEnabled)
            throw new ServiceException("route_change_unavailable", "تغيير المسار متاح أثناء الرحلة وقبل فتح الدفع.");
        if (!ride.HasRoutePricingSnapshot)
            throw new ServiceException("route_pricing_unavailable", "هذه الرحلة سابقة لتحديث تسعير تغيير المسار؛ لا يمكن إعادة تسعيرها دون سياسة أصلية محفوظة.");
        if (await db.RideWaitingSessions.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct))
            throw new ServiceException("waiting_must_end", "استأنف الرحلة وأغلق الانتظار قبل تغيير المسار.");
        if (await db.PaymentWalletOperations.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct) ||
            await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && (x.Status == PaymentStatus.Pending || x.Status == PaymentStatus.Authorized || x.Status == PaymentStatus.Paid), ct) ||
            await db.CashPaymentRequests.AnyAsync(x => x.RideId == ride.Id && (x.Status == CashPaymentRequestStatus.Pending || x.Status == CashPaymentRequestStatus.DriverConfirmed), ct) ||
            await db.CashCollectionApprovals.AnyAsync(x => x.RideId == ride.Id && (x.Status == CashCollectionApprovalStatus.Pending || x.Status == CashCollectionApprovalStatus.Approved), ct))
            throw new ServiceException("payment_already_open", "لا يمكن تغيير المسار بعد فتح طلب دفع أو تحصيل.");
    }
    private static void Finish(RideRouteChange request, string status, DateTime now)
    { request.IsOpen = false; request.Status = status; request.DecidedAtUtc = now; request.UpdatedAtUtc = now; }
    private void Notify(RideEntity ride, int requestId, string title, string message)
    {
        foreach (var userId in new[] { ride.CustomerId, ride.DriverId ?? ride.CustomerId }.Distinct())
            db.Notifications.Add(new Notification
            {
                UserId = userId, Type = NotificationType.RideStatus, Title = title, Body = message,
                DataJson = $"{{\"action\":\"open_active_ride\",\"rideId\":{ride.Id},\"routeChangeId\":{requestId}}}"
            });
    }

    private async Task<object> SnapshotAsync(RideEntity ride, int userId, bool admin, RideRouteChange? requested, CancellationToken ct)
    {
        var history = await db.RideRouteChanges.AsNoTracking().Where(x => x.RideId == ride.Id && (admin || x.Status != "quoted" || x.RequestedByUserId == userId))
            .OrderByDescending(x => x.Id).Take(50).ToListAsync(ct);
        var current = requested ?? history.FirstOrDefault(x => x.IsOpen) ?? history.FirstOrDefault();
        var unavailable = ride.Status != RideStatus.InProgress ? "تغيير الوجهة متاح أثناء الرحلة." :
            !ride.HasRoutePricingSnapshot ? "هذه الرحلة لا تحتوي سياسة أصلية محفوظة لتغيير المسار." :
            ride.CustomerPaymentEnabled ? "بدأت مرحلة الدفع؛ تغيير الوجهة غير متاح." : null;
        if (unavailable == null && await db.RideWaitingSessions.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct))
            unavailable = "استأنف الرحلة وأغلق الانتظار قبل تغيير الوجهة.";
        if (unavailable == null && (await db.PaymentWalletOperations.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct) ||
            await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && (x.Status == PaymentStatus.Pending || x.Status == PaymentStatus.Authorized || x.Status == PaymentStatus.Paid), ct) ||
            await db.CashPaymentRequests.AnyAsync(x => x.RideId == ride.Id && (x.Status == CashPaymentRequestStatus.Pending || x.Status == CashPaymentRequestStatus.DriverConfirmed), ct) ||
            await db.CashCollectionApprovals.AnyAsync(x => x.RideId == ride.Id && (x.Status == CashCollectionApprovalStatus.Pending || x.Status == CashCollectionApprovalStatus.Approved), ct)))
            unavailable = "يوجد طلب دفع أو تحصيل لهذه الرحلة؛ تغيير الوجهة غير متاح.";
        var blocked = await db.RideWaitingSessions.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct) ||
            await db.RideRouteChanges.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct);
        return new { rideId = ride.Id, serverNowUtc = DateTime.UtcNow, ride.RouteVersion,
            canRequest = userId == ride.CustomerId && unavailable == null && !blocked, unavailableReason = unavailable,
            request = current == null ? null : View(current, userId, ride.DriverId), history = history.Select(x => View(x, userId, ride.DriverId)) };
    }
    public static object Invoice(RideRouteChange x) => new { x.PreviousTotal, x.CompletedDistanceMeters, x.CompletedDurationSeconds,
        x.CompletedFare, x.OriginalServiceFee, x.RouteChangeFee, x.PreviousRouteChangeFees, x.NewDistanceMeters, x.NewDurationSeconds,
        x.NewRouteFare, x.WaitingFee, x.NewTotal, difference = x.NewTotal - x.PreviousTotal };
    public static object View(RideRouteChange x, int userId, int? assignedDriverId = null) => new { x.Id, x.Status, requestedByMe = x.RequestedByUserId == userId,
        canConfirm = x.IsOpen && x.Status == "quoted" && x.RequestedByUserId == userId,
        canDecide = x.IsOpen && x.Status == "pending" && assignedDriverId == userId && x.RequestedByUserId != userId,
        x.ExpiresAtUtc, x.CreatedAtUtc, x.DecidedAtUtc, x.NewDestinationLabel, x.NewDestinationLatitude, x.NewDestinationLongitude,
        x.ChangeLatitude, x.ChangeLongitude, invoice = Invoice(x) };
}
