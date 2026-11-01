using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

/// <summary>
/// Stops a ride first, then records a reviewable cancellation case. No wallet
/// or ledger movement is made here; administration remains the sole authority
/// for a refund decision.
/// </summary>
public sealed class RideCancellationRequest(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser,
    RideAccountingPostingService accounting,
    IRideRealtimePublisher? realtimePublisher = null) : OperationsService<RideCancellationRequestModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["add", "get", "update", "accept", "reject", "refer", "adminApprove", "adminReject"];

    protected override async Task<object?> AddAsync(RideCancellationRequestModel model, CancellationToken token)
    {
        var customerId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كعميل.");
        var reason = model.Reason?.Trim();
        if (model.RideId is null || string.IsNullOrWhiteSpace(reason) || reason.Length < 3)
            throw new ServiceException("cancellation_reason_required", "حدد سبب الإلغاء أو اكتبه بوضوح قبل إرسال الطلب.");
        if (reason.Length > 1000) throw new ServiceException("invalid_cancellation_reason", "سبب الإلغاء أطول من الحد المسموح.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await RidePaymentGuard.LockAsync(db, model.RideId.Value, token);
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.Id == model.RideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != customerId && ride.DriverId != customerId)
            throw new ServiceException("ride_access_denied", "لا تملك صلاحية إلغاء هذه الرحلة.");
        await WalletPaymentCancellationPolicy.CancelUnsubmittedAsync(db, ride.Id, token);
        if (ride.Status == RideStatus.Completed)
            throw new ServiceException("completed_ride_not_cancellable", "لا يمكن إلغاء رحلة مكتملة أو طلب استرداد لها.");
        if (ride.Status == RideStatus.Cancelled)
            throw new ServiceException("ride_not_cancellable", "هذه الرحلة ملغاة بالفعل.");
        if (await db.RideCancellationRequests.AnyAsync(
                x => x.RideId == ride.Id && (x.Status == RideCancellationStatus.DriverReviewPending ||
                    x.Status == RideCancellationStatus.AdminReviewPending || x.Status == RideCancellationStatus.CustomerReviewPending ||
                    x.Status == RideCancellationStatus.RefundMethodPending || x.Status == RideCancellationStatus.RefundConsentPending), token))
            throw new ServiceException("ride_cancellation_already_open", "هذه الرحلة لديها طلب إلغاء قيد المراجعة.");

        if (customerId == ride.DriverId)
        {
            var driverRequest = await AddByDriverAsync(ride, reason, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            if (realtimePublisher is not null)
                await realtimePublisher.PublishRideStatusUpdatedAsync(RideRealtimeEventFactory.Status(ride), token);
            return await ToResultAsync(driverRequest, token);
        }

        var isInProgress = ride.Status == RideStatus.InProgress || ride.StartedAtUtc is not null;
        var hasPaidPayment = await db.PaymentTransactions.AnyAsync(
            x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid, token);
        var paymentPending = await db.PaymentWalletOperations.AnyAsync(x => x.RideId == ride.Id && x.IsOpen && x.Status != "Succeeded", token);
        var directEnRouteCancellation = ride.Status is RideStatus.DriverAssigned or RideStatus.DriverEnRoute && !hasPaidPayment && !paymentPending;
        var rejections = await db.RideCancellationRequests.CountAsync(
            x => x.RideId == ride.Id && x.DriverDecision == RideCancellationDriverDecision.Rejected, token);
        // A started ride is first presented to its driver. Only the third
        // driver rejection (or an unassigned ride) escalates to administration.
        var reviewByAdmin = ride.DriverId is null || rejections >= 3;
        var request = new YemenDrive.Database.Entities.RideCancellationRequest
        {
            RideId = ride.Id,
            CustomerId = customerId,
            DriverId = ride.DriverId,
            RideStatusAtRequest = ride.Status,
            Status = directEnRouteCancellation ? RideCancellationStatus.AdminApproved : reviewByAdmin ? RideCancellationStatus.AdminReviewPending : RideCancellationStatus.DriverReviewPending,
            DriverDecision = directEnRouteCancellation ? RideCancellationDriverDecision.Accepted : RideCancellationDriverDecision.None,
            Reason = reason,
            RequestedRefundMethod = model.RequestedRefundMethod ?? CashCancellationRefundMethod.CreditCustomerWallet
        };

        if (isInProgress)
        {
            var latestLocation = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == ride.Id && x.ActorId == ride.DriverId)
                .OrderByDescending(x => x.ObservedAtUtc).FirstOrDefaultAsync(token);
            if (latestLocation is not null)
            {
                request.CancellationLatitude = latestLocation.Latitude;
                request.CancellationLongitude = latestLocation.Longitude;
                request.LocationObservedAtUtc = latestLocation.ObservedAtUtc;
            }
            else if (ride.DriverId is int driverId)
            {
                var live = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == driverId, token);
                if (live is not null)
                {
                    request.CancellationLatitude = live.Latitude;
                    request.CancellationLongitude = live.Longitude;
                    request.LocationObservedAtUtc = live.ObservedAtUtc;
                }
            }
        }

        var waiting = new RideWaitingCoordinator(db);
        var stop = await waiting.ReconcileAsync(ride, DateTime.UtcNow, token);
        if (stop?.IsOpen == true) waiting.Close(ride, stop, DateTime.UtcNow, "ride_cancelled");
        if (hasPaidPayment)
        {
            var quote = await RideCancellationQuote.GetAsync(db, ride, token, request);
            request.RequestedRefundAmount = quote.RefundAmount;
            if (quote.ObservedAtUtc is not null)
            {
                request.CancellationLatitude = quote.Latitude;
                request.CancellationLongitude = quote.Longitude;
                request.LocationObservedAtUtc = quote.ObservedAtUtc;
            }
        }
        ride.Status = directEnRouteCancellation ? RideStatus.Cancelled : RideStatus.CancellationPending;
        ride.UpdatedAtUtc = DateTime.UtcNow;
        if (directEnRouteCancellation) await RideDriverAvailability.ReleaseAsync(db, ride, token);
        db.RideCancellationRequests.Add(request);
        await db.SaveChangesAsync(token);
        if (directEnRouteCancellation)
        {
            if (ride.DriverId is int directDriverId)
                db.Notifications.Add(new Notification { UserId = directDriverId, Type = NotificationType.RideStatus, Title = "تم إلغاء الرحلة", Body = "ألغى العميل الرحلة قبل بدءها. يمكنك إيقاف التوجه إلى نقطة الانطلاق.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
            db.Notifications.Add(new Notification { UserId = customerId, Type = NotificationType.RideStatus, Title = "تم إلغاء الرحلة", Body = "تم إلغاء الرحلة بناءً على السبب الذي اخترته.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
        }
        else if (reviewByAdmin)
        {
            await NotifyAdminsAsync(ride, request, isInProgress ? "بدأت الرحلة؛ أُحيل طلب الإلغاء مباشرةً إلى الإدارة." : "طلب الإلغاء يحتاج مراجعة الإدارة.", token);
            db.Notifications.Add(new Notification { UserId = customerId, Type = NotificationType.RideStatus, Title = "طلب الإلغاء قيد المراجعة", Body = "أُوقفت الرحلة وأُحيل طلبك إلى الإدارة. سيصلك إشعار بالقرار.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
        }
        else
        {
            db.Notifications.Add(new Notification { UserId = ride.DriverId!.Value, Type = NotificationType.RideStatus, Title = "طلب إلغاء رحلة", Body = "طلب العميل إلغاء الرحلة. راجع السبب واختر القبول أو الرفض أو الإحالة للإدارة.", DataJson = $"{{\"action\":\"review_cancellation_request\",\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
            db.Notifications.Add(new Notification { UserId = customerId, Type = NotificationType.RideStatus, Title = "أُوقفت الرحلة", Body = "أُرسل طلب الإلغاء إلى السائق للمراجعة.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher is not null)
            await realtimePublisher.PublishRideStatusUpdatedAsync(
                RideRealtimeEventFactory.Status(ride), token);
        return await ToResultAsync(request, token);
    }

    protected override async Task<object?> GetAsync(RideCancellationRequestModel model, CancellationToken token)
    {
        if (model.Id is null && model.RideId is null) throw new ServiceException("id_required", "معرف طلب الإلغاء أو الرحلة مطلوب.");
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        var isAdmin = await IsAdminAsync(userId, token);
        if (model.PreviewOnly)
        {
            var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.RideId, token)
                ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
            if (!isAdmin && ride.CustomerId != userId && ride.DriverId != userId)
                throw new ServiceException("ride_access_denied", "لا تملك صلاحية مراجعة استرداد هذه الرحلة.");
            return await RideCancellationQuote.GetAsync(db, ride, token,
                reviewedTravelledFare: isAdmin ? model.ReviewedTravelledFare : null);
        }
        var query = db.RideCancellationRequests.AsNoTracking().Include(x => x.Ride).AsQueryable();
        if (model.Id is int id) query = query.Where(x => x.Id == id);
        else query = query.Where(x => x.RideId == model.RideId);
        var request = await query.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        if (!isAdmin && request.CustomerId != userId && request.DriverId != userId)
            throw new ServiceException("cancellation_request_access_denied", "لا تملك صلاحية الوصول إلى طلب الإلغاء.");
        return await ToResultAsync(request, token);
    }

    protected override Task<object?> AcceptAsync(RideCancellationRequestModel model, CancellationToken token) => DecideAsync(model, true, token);
    protected override Task<object?> RejectAsync(RideCancellationRequestModel model, CancellationToken token) => DecideAsync(model, false, token);
    protected override Task<object?> ReferAsync(RideCancellationRequestModel model, CancellationToken token) => DriverDecideAsync(model, RideCancellationDriverDecision.ReferredToAdmin, token);
    protected override Task<object?> AdminApproveAsync(RideCancellationRequestModel model, CancellationToken token) => AdminDecideAsync(model, true, token);
    protected override Task<object?> AdminRejectAsync(RideCancellationRequestModel model, CancellationToken token) => AdminDecideAsync(model, false, token);

    private async Task<object?> DriverDecideAsync(RideCancellationRequestModel model, RideCancellationDriverDecision decision, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف طلب الإلغاء مطلوب.");
        var driverId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كسائق.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockRequestRideAsync(model.Id, token);
        var request = await db.RideCancellationRequests.Include(x => x.Ride).SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        if (request.DriverId != driverId) throw new ServiceException("cancellation_driver_access_denied", "هذا الطلب ليس موجهاً للسائق الحالي.");
        if (request.Status == RideCancellationStatus.CustomerReviewPending)
            throw new ServiceException("cancellation_customer_access_denied", "قرار طلب الإلغاء الذي قدمه السائق متاح للراكب فقط.");
        if (request.Status != RideCancellationStatus.DriverReviewPending) return await ToResultAsync(request, token);
        var previousRideStatus = request.Ride.Status;
        request.DriverDecision = decision;
        request.DriverDecidedAtUtc = DateTime.UtcNow;
        request.DriverNote = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim();
        if (decision == RideCancellationDriverDecision.Rejected)
        {
            var rejectionCount = await db.RideCancellationRequests.CountAsync(
                x => x.RideId == request.RideId && x.DriverDecision == RideCancellationDriverDecision.Rejected, token) + 1;
            if (rejectionCount >= 3)
            {
                request.Status = RideCancellationStatus.AdminReviewPending;
                await NotifyAdminsAsync(request.Ride, request, "رفض السائق طلب الإلغاء للمرة الثالثة؛ أُحيل الطلب إلى الإدارة.", token);
                db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "أُحيل طلب الإلغاء إلى الإدارة", Body = "رفض السائق طلب الإلغاء ثلاث مرات، لذلك أُحيل الطلب إلى الإدارة للمراجعة.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
            else
            {
                request.Status = RideCancellationStatus.DriverRejected;
                db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "رفض السائق طلب الإلغاء", Body = $"رفض السائق طلب الإلغاء. يمكنك إرسال طلب جديد؛ بعد {3 - rejectionCount} رفضاً إضافياً يُحال الطلب إلى الإدارة.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
        }
        else if (decision == RideCancellationDriverDecision.Accepted)
        {
            var hasPaidPayment = await db.PaymentTransactions.AnyAsync(
                x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid, token);
            var paymentPending = await db.PaymentWalletOperations.AnyAsync(x => x.RideId == request.RideId && x.IsOpen && x.Status != "Succeeded", token);
            if (!hasPaidPayment && !paymentPending)
            {
                request.Status = RideCancellationStatus.AdminApproved;
                request.Ride.Status = RideStatus.Cancelled;
                request.Ride.UpdatedAtUtc = DateTime.UtcNow;
                await RideDriverAvailability.ReleaseAsync(db, request.Ride, token);
                db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "تم إلغاء الرحلة", Body = "وافق السائق على إلغاء الرحلة، وتم إلغاؤها.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
                db.Notifications.Add(new Notification { UserId = request.DriverId!.Value, Type = NotificationType.RideStatus, Title = "تم إلغاء الرحلة", Body = "تم إلغاء الرحلة بناءً على موافقتك.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
            else
            {
                request.Status = request.AdminDecidedAtUtc is not null ? RideCancellationStatus.RefundMethodPending : RideCancellationStatus.AdminReviewPending;
                if (request.Status == RideCancellationStatus.AdminReviewPending)
                    await NotifyAdminsAsync(request.Ride, request, "وافق السائق على الإلغاء؛ بانتظار قرار الإدارة المالي.", token);
                else await NotifyRefundMethodAsync(request, token);
                db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "طلب الإلغاء قيد مراجعة الإدارة", Body = "أُحيل طلب الإلغاء إلى الإدارة لمراجعة الاسترداد المالي. سيصلك إشعار بالقرار.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
        }
        else
        {
            request.Status = RideCancellationStatus.AdminReviewPending;
            await NotifyAdminsAsync(request.Ride, request, "أحال السائق طلب الإلغاء إلى الإدارة.", token);
            db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "طلب الإلغاء قيد مراجعة الإدارة", Body = "أُحيل طلب الإلغاء إلى الإدارة. سيصلك إشعار بالقرار.", DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher is not null && request.Ride.Status != previousRideStatus)
            await realtimePublisher.PublishRideStatusUpdatedAsync(
                RideRealtimeEventFactory.Status(request.Ride), token);
        return await ToResultAsync(request, token);
    }

    private async Task<object?> AdminDecideAsync(RideCancellationRequestModel model, bool approve, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف طلب الإلغاء مطلوب.");
        var adminId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كمدير.");
        if (!await IsAdminAsync(adminId, token)) throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockRequestRideAsync(model.Id, token);
        var request = await db.RideCancellationRequests.Include(x => x.Ride).SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        var customerApprovedRefund = request.Status == RideCancellationStatus.CustomerApproved && request.AdminDecidedAtUtc == null &&
            await db.PaymentTransactions.AnyAsync(x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid, token);
        if (request.Status != RideCancellationStatus.AdminReviewPending && !customerApprovedRefund) return await ToResultAsync(request, token);
        if (approve) await EnsureWalletPaymentSettledAsync(request.RideId, token);
        var previousRideStatus = request.Ride.Status;
        if (!customerApprovedRefund) request.Status = approve ? RideCancellationStatus.AdminApproved : RideCancellationStatus.AdminRejected;
        request.AdminUserId = adminId;
        request.AdminDecidedAtUtc = DateTime.UtcNow;
        request.AdminNote = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim();
        CancellationSettlementResult? settlement = null;
        if (approve)
        {
            // A paid cancellation is intentionally settled only at this
            // administrative decision point.  The original payment remains
            // immutable; this creates its own refund/payment record, wallet
            // movement when applicable, driver receivable and reversing
            // journal entry in the same serializable transaction.
            var cashPaid = await db.PaymentTransactions.AnyAsync(x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid && x.Provider == "Cash", token);
            if (cashPaid)
            {
                var quote = await RideCancellationQuote.GetAsync(db, request.Ride, token, request, model.ReviewedTravelledFare);
                if (quote.RefundAmount is null) throw new ServiceException("cancellation_refund_review_required", quote.CalculationMessage ?? "راجع أجرة الجزء المقطوع قبل الاعتماد.");
                request.RequestedRefundAmount = quote.RefundAmount;
                request.Status = RideCancellationStatus.RefundMethodPending;
                await NotifyRefundMethodAsync(request, token);
            }
            else
            {
                settlement = await SettlePaidCancellationAsync(request, adminId, token, model.ReviewedTravelledFare);
                request.Ride.Status = RideStatus.Cancelled;
                request.Ride.UpdatedAtUtc = DateTime.UtcNow;
                await RideDriverAvailability.ReleaseAsync(db, request.Ride, token);
            }
        }
        var customerBody = !approve
            ? "راجعت الإدارة الطلب ولم توافق على الإلغاء المالي."
            : request.Status == RideCancellationStatus.RefundMethodPending
                ? "وافقت الإدارة على الإلغاء. يحدد السائق طريقة إعادة صافي المبلغ قبل الإلغاء النهائي."
            : settlement is null
                ? "وافقت الإدارة على طلب الإلغاء وتم إلغاء الرحلة."
                : settlement.CreditedToWallet
                    ? $"وافقت الإدارة على الإلغاء وأُضيف {settlement.RefundAmount:0.##} {settlement.Currency} إلى محفظتك."
                    : "وافقت الإدارة على الإلغاء وسُجل الاسترداد النقدي المباشر من السائق.";
        db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = approve ? "تمت الموافقة على الإلغاء" : "رُفض طلب الإلغاء", Body = customerBody, DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id},\"approved\":{approve.ToString().ToLowerInvariant()},\"settled\":{(settlement is not null).ToString().ToLowerInvariant()}}}" });
        if (request.DriverId is int driverId)
        {
            var driverBody = !approve
                ? "رفضت الإدارة طلب الإلغاء."
                : settlement?.DriverDebtCreated == true
                    ? $"وافقت الإدارة على الإلغاء وأُعيد {settlement.RefundAmount:0.##} {settlement.Currency} إلى محفظة العميل وسُجلت مديونية مستقلة عليك بقيمة {settlement.DriverDebtAmount:0.##}."
                    : settlement is not null && settlement.RefundMethod == CashCancellationRefundMethod.ReturnFromDriver
                        ? "وافقت الإدارة على الإلغاء. سجّل العميل استرداد مبلغ الرحلة منك نقداً مباشرة."
                        : "وافقت الإدارة على إلغاء الرحلة.";
            db.Notifications.Add(new Notification { UserId = driverId, Type = NotificationType.RideStatus, Title = "قرار الإدارة في طلب الإلغاء", Body = driverBody, DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id},\"settled\":{(settlement is not null).ToString().ToLowerInvariant()}}}" });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher is not null && request.Ride.Status != previousRideStatus)
            await realtimePublisher.PublishRideStatusUpdatedAsync(
                RideRealtimeEventFactory.Status(request.Ride), token);
        if (settlement is not null && realtimePublisher is not null)
        {
            var recipients = request.DriverId is int assignedDriverId
                ? new[] { request.CustomerId, assignedDriverId }
                : new[] { request.CustomerId };
            await realtimePublisher.PublishPaymentUpdatedAsync(
                recipients,
                new PaymentRealtimeEvent(
                    request.RideId, "paymentRefunded", DateTime.UtcNow),
                token);
        }
        return await ToResultAsync(request, token);
    }

    private async Task<CancellationSettlementResult?> SettlePaidCancellationAsync(
        YemenDrive.Database.Entities.RideCancellationRequest request,
        int actorUserId,
        CancellationToken token,
        decimal? reviewedTravelledFare = null)
    {
        var payment = await db.PaymentTransactions.SingleOrDefaultAsync(
            x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid, token);
        if (payment is null) return null;
        var ride = request.Ride;
        if (ride.DriverId is null)
            throw new ServiceException("driver_not_assigned", "لا يمكن تسوية إلغاء رحلة مدفوعة دون سائق مسند.");

        var quote = await RideCancellationQuote.GetAsync(db, ride, token, request, reviewedTravelledFare);
        if (quote.RefundAmount is not { } refundAmount)
            throw new ServiceException("cancellation_refund_review_required", quote.CalculationMessage ?? "حدد أجرة الجزء المقطوع بعد مراجعة الإدارة قبل اعتماد الاسترداد.");
        request.RequestedRefundAmount = refundAmount;
        if (string.Equals(payment.Provider, "Cash", StringComparison.OrdinalIgnoreCase))
            return await SettleCashCancellationAsync(request, payment, refundAmount, actorUserId, token);
        if (payment.Provider is "YemenDriveWallet" or "Floosak" or "Jaib")
            return await SettleWalletCancellationAsync(request, payment, refundAmount, actorUserId, token);

        throw new ServiceException("manual_refund_required", "استرداد وسيلة الدفع هذه يحتاج معالجة مالية يدوية موثقة.");
    }

    private async Task<CancellationSettlementResult> SettleCashCancellationAsync(
        YemenDrive.Database.Entities.RideCancellationRequest request,
        PaymentTransaction originalPayment,
        decimal refundAmount,
        int actorUserId,
        CancellationToken token)
    {
        var refundMethod = request.RequestedRefundMethod
            ?? throw new ServiceException("cash_refund_method_required", "حدد العميل مسار استرداد الدفع النقدي قبل اعتماد الإلغاء.");
        var refundReference = $"cash-cancellation:payment:{originalPayment.Id}:{refundMethod}";
        if (await db.PaymentTransactions.AnyAsync(x => x.ProviderReference == refundReference, token))
            throw new ServiceException("refund_already_processed", "تمت معالجة استرداد هذه الدفعة مسبقاً.");

        var ride = request.Ride;
        var creditedToWallet = refundMethod == CashCancellationRefundMethod.CreditCustomerWallet;
        if (creditedToWallet && refundAmount > 0)
        {
            var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == ride.CustomerId, token)
                ?? throw new ServiceException("wallet_not_found", "محفظة العميل غير موجودة.");
            if (!string.Equals(wallet.Currency, originalPayment.Currency, StringComparison.OrdinalIgnoreCase))
                throw new ServiceException("currency_mismatch", "عملة الاسترداد لا تطابق عملة محفظة العميل.");
            wallet.Balance += refundAmount;
            db.WalletTransactions.Add(new WalletTransaction
            {
                WalletId = wallet.Id, RideId = ride.Id, Type = WalletTransactionType.Refund,
                Amount = refundAmount, BalanceAfter = wallet.Balance,
                Description = "استرداد رحلة نقدية ملغاة إلى محفظة العميل بعد خصم رسم الإلغاء",
                ExternalReference = refundReference
            });
            // The driver retains the cash while the platform funds the wallet;
            // record that separate receivable without changing the collection
            // debt created by the original cash payment.
            db.DriverSettlements.Add(new DriverSettlement
            {
                DriverId = ride.DriverId!.Value,
                PeriodStartUtc = DateTime.UtcNow, PeriodEndUtc = DateTime.UtcNow,
                GrossRideAmount = 0, PlatformCommission = 0, Adjustments = -refundAmount,
                NetPayable = -refundAmount, Status = PaymentStatus.Pending,
                PaymentReference = refundReference
            });
        }

        if (refundAmount > 0)
        {
            db.PaymentTransactions.Add(new PaymentTransaction
            {
                UserId = ride.CustomerId, RideId = ride.Id, Amount = refundAmount,
                Currency = originalPayment.Currency,
                Provider = creditedToWallet ? "CashRefundToCustomerWallet" : "CashReturnFromDriver",
                Status = PaymentStatus.Refunded, ProviderReference = refundReference
            });
        }
        await accounting.PostCancellationAsync(ride, originalPayment, refundAmount, creditedToWallet,
            driverCommissionReversal: 0, actorUserId, token);
        return new CancellationSettlementResult(refundAmount, originalPayment.Currency, refundMethod,
            creditedToWallet, creditedToWallet && refundAmount > 0, creditedToWallet ? refundAmount : 0m);
    }

    private async Task<CancellationSettlementResult> SettleWalletCancellationAsync(
        YemenDrive.Database.Entities.RideCancellationRequest request,
        PaymentTransaction originalPayment,
        decimal refundAmount,
        int actorUserId,
        CancellationToken token)
    {
        var refundReference = $"refund:payment:{originalPayment.Id}";
        if (await db.PaymentTransactions.AnyAsync(x => x.ProviderReference == refundReference, token))
            throw new ServiceException("refund_already_processed", "تمت معالجة استرداد هذه الدفعة مسبقاً.");
        var ride = request.Ride;
        var customerWallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == ride.CustomerId, token)
            ?? throw new ServiceException("wallet_not_found", "محفظة العميل غير موجودة.");
        var driverWallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == ride.DriverId, token)
            ?? throw new ServiceException("driver_wallet_not_found", "محفظة السائق غير موجودة.");
        if (!string.Equals(customerWallet.Currency, originalPayment.Currency, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(driverWallet.Currency, originalPayment.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException("currency_mismatch", "عملة الاسترداد لا تطابق محافظ أطراف الرحلة.");

        var originalFare = Math.Max(0m, (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) + ride.WaitingFee);
        var retainedFare = Math.Clamp(originalPayment.Amount - refundAmount - ride.ServiceFee - ride.CancellationFee, 0m, originalFare);
        var driverNet = Math.Max(0m, ride.DriverShare ?? originalFare - ride.DriverCommissionAmount);
        var retainedDriverNet = originalFare == 0m ? 0m : decimal.Round(driverNet * retainedFare / originalFare, 2, MidpointRounding.AwayFromZero);
        var driverReversal = Math.Max(0m, driverNet - retainedDriverNet);
        var walletDebit = Math.Min(Math.Max(0m, driverWallet.Balance), driverReversal);
        var driverDebt = driverReversal - walletDebit;
        customerWallet.Balance += refundAmount;
        driverWallet.Balance -= walletDebit;
        if (refundAmount > 0)
        {
            db.WalletTransactions.Add(new WalletTransaction { WalletId = customerWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Refund, Amount = refundAmount, BalanceAfter = customerWallet.Balance, Description = "استرداد الإلغاء بعد خصم الرسوم وأجرة الجزء المقطوع", ExternalReference = refundReference });
            db.PaymentTransactions.Add(new PaymentTransaction { UserId = ride.CustomerId, RideId = ride.Id, Amount = refundAmount, Currency = originalPayment.Currency, Provider = originalPayment.Provider == "YemenDriveWallet" ? "YemenDriveWallet" : "ExternalWalletRefundToCustomerWallet", Status = PaymentStatus.Refunded, ProviderReference = refundReference });
        }
        if (walletDebit > 0)
            db.WalletTransactions.Add(new WalletTransaction { WalletId = driverWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Debit, Amount = walletDebit, BalanceAfter = driverWallet.Balance, Description = "عكس الجزء غير المستحق للسائق بسبب الإلغاء", ExternalReference = refundReference });
        foreach (var reversal in new[] { (Amount: walletDebit, Status: PaymentStatus.Refunded, Suffix: "wallet"),
            (Amount: driverDebt, Status: PaymentStatus.Pending, Suffix: "debt") })
            if (reversal.Amount > 0) db.DriverSettlements.Add(new DriverSettlement
            {
                DriverId = ride.DriverId!.Value, PeriodStartUtc = DateTime.UtcNow, PeriodEndUtc = DateTime.UtcNow,
                GrossRideAmount = 0, PlatformCommission = 0, Adjustments = -reversal.Amount,
                NetPayable = -reversal.Amount, Status = reversal.Status,
                PaymentReference = refundReference + ":driver-" + reversal.Suffix
            });
        await accounting.PostCancellationAsync(ride, originalPayment, refundAmount, true, driverReversal, actorUserId, token,
            driverWalletDebit: walletDebit);
        return new CancellationSettlementResult(refundAmount, originalPayment.Currency, null, true, driverDebt > 0, driverDebt);
    }

    private async Task NotifyAdminsAsync(YemenDrive.Database.Entities.Ride ride, YemenDrive.Database.Entities.RideCancellationRequest request, string body, CancellationToken token)
    {
        var adminIds = await db.Users.AsNoTracking().Where(x => x.Role == UserRole.Admin && x.IsActive).Select(x => x.Id).ToListAsync(token);
        foreach (var adminId in adminIds)
            db.Notifications.Add(new Notification { UserId = adminId, Type = NotificationType.RideStatus, Title = "طلب إلغاء يحتاج مراجعة", Body = body, DataJson = $"{{\"action\":\"open_cancellation_request\",\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
    }

    private Task<bool> IsAdminAsync(int userId, CancellationToken token) => db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, token);

    private async Task<object> ToResultAsync(YemenDrive.Database.Entities.RideCancellationRequest x, CancellationToken token)
    {
      return new {
        x.Id, x.RideId, x.CustomerId, x.DriverId, x.RideStatusAtRequest, x.Status, x.DriverDecision,
        requestedByDriver = IsDriverRequest(x),
        canChooseRefundMethod = x.Status == RideCancellationStatus.RefundMethodPending && currentUser.UserId == x.DriverId,
        refundConsentPending = x.Status == RideCancellationStatus.RefundConsentPending,
        x.RefundChoiceRevision,
        canDecide = x.Status is RideCancellationStatus.CustomerReviewPending or RideCancellationStatus.RefundConsentPending ? currentUser.UserId == x.CustomerId :
            x.Status == RideCancellationStatus.DriverReviewPending && currentUser.UserId == x.DriverId,
        rideStatus = x.Ride.Status,
        x.Reason, x.RequestedRefundMethod, x.RequestedRefundAmount, x.CancellationLatitude, x.CancellationLongitude,
        x.LocationObservedAtUtc, x.DriverDecidedAtUtc, x.DriverNote, x.AdminUserId, x.AdminDecidedAtUtc, x.AdminNote, x.CreatedAtUtc,
        refundQuote = await RideCancellationQuote.GetAsync(db, x.Ride, token, x)
      };
    }

    private static bool IsDriverRequest(YemenDrive.Database.Entities.RideCancellationRequest request) =>
        request.RequestedByDriver || request.Status is RideCancellationStatus.CustomerReviewPending or RideCancellationStatus.CustomerApproved or
            RideCancellationStatus.CustomerRejected or RideCancellationStatus.DriverCancelled;

    private async Task<YemenDrive.Database.Entities.RideCancellationRequest> AddByDriverAsync(
        YemenDrive.Database.Entities.Ride ride, string reason, CancellationToken token)
    {
        var pending = ride.Status == RideStatus.InProgress;
        if (!pending && ride.Status is not (RideStatus.DriverAssigned or RideStatus.DriverEnRoute or RideStatus.DriverArrived))
            throw new ServiceException("ride_not_cancellable", "لا يمكن للسائق طلب الإلغاء في هذه المرحلة.");
        var paid = await db.PaymentTransactions.AnyAsync(
            x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid, token);
        var paymentPending = await db.PaymentWalletOperations.AnyAsync(x => x.RideId == ride.Id && x.IsOpen && x.Status != "Succeeded", token);
        pending = pending || paid || paymentPending;
        var request = new YemenDrive.Database.Entities.RideCancellationRequest
        {
            RideId = ride.Id, Ride = ride, CustomerId = ride.CustomerId, DriverId = ride.DriverId, RequestedByDriver = true,
            RideStatusAtRequest = ride.Status, Reason = reason,
            Status = pending ? RideCancellationStatus.CustomerReviewPending : RideCancellationStatus.DriverCancelled,
            RequestedRefundMethod = CashCancellationRefundMethod.CreditCustomerWallet
        };
        var waiting = new RideWaitingCoordinator(db);
        var stop = await waiting.ReconcileAsync(ride, DateTime.UtcNow, token);
        if (stop?.IsOpen == true) waiting.Close(ride, stop, DateTime.UtcNow, "ride_cancelled");
        if (paid)
        {
            var quote = await RideCancellationQuote.GetAsync(db, ride, token, request);
            request.RequestedRefundAmount = quote.RefundAmount;
            request.CancellationLatitude = quote.Latitude;
            request.CancellationLongitude = quote.Longitude;
            request.LocationObservedAtUtc = quote.ObservedAtUtc;
        }
        db.RideCancellationRequests.Add(request);
        if (!pending)
        {
            ride.Status = RideStatus.Cancelled;
            await RideDriverAvailability.ReleaseAsync(db, ride, token);
        }
        ride.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        db.Notifications.Add(new Notification
        {
            UserId = ride.CustomerId, Type = NotificationType.RideStatus,
            Title = pending ? "طلب السائق إلغاء الرحلة" : "ألغى السائق الرحلة",
            Body = pending ? "يرجى مراجعة سبب الإلغاء. لا يكتمل الإلغاء قبل الموافقات والتسوية المطلوبة." : "ألغى السائق الرحلة قبل بدءها.",
            DataJson = $"{{\"action\":\"{(pending ? "review_cancellation_request" : "open_active_ride")}\",\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}"
        });
        return request;
    }

    private async Task<object?> DecideAsync(RideCancellationRequestModel model, bool approve, CancellationToken token)
    {
        var request = await db.RideCancellationRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        if (request.Status == RideCancellationStatus.AdminApproved &&
            (currentUser.UserId == request.CustomerId || currentUser.UserId == request.DriverId))
        {
            var completed = await db.RideCancellationRequests.Include(x => x.Ride).SingleAsync(x => x.Id == model.Id, token);
            return await ToResultAsync(completed, token);
        }
        return request.Status == RideCancellationStatus.RefundConsentPending ? await RefundConsentAsync(model, approve, token) :
            IsDriverRequest(request) ? await CustomerDecideAsync(model, approve, token) :
            await DriverDecideAsync(model, approve ? RideCancellationDriverDecision.Accepted : RideCancellationDriverDecision.Rejected, token);
    }

    private async Task<object?> CustomerDecideAsync(RideCancellationRequestModel model, bool approve, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockRequestRideAsync(model.Id, token);
        var request = await db.RideCancellationRequests.Include(x => x.Ride).SingleAsync(x => x.Id == model.Id, token);
        if (currentUser.UserId != request.CustomerId)
            throw new ServiceException("cancellation_customer_access_denied", "الموافقة على طلب السائق متاحة لراكب الرحلة فقط.");
        if (request.Status != RideCancellationStatus.CustomerReviewPending) return await ToResultAsync(request, token);
        var ride = request.Ride;
        if (ride.Status is not (RideStatus.InProgress or RideStatus.DriverAssigned or RideStatus.DriverEnRoute or RideStatus.DriverArrived))
            throw new ServiceException("ride_not_cancellable", "لم تعد الرحلة جارية؛ لا يمكن تطبيق طلب الإلغاء القديم.");
        request.Status = approve ? RideCancellationStatus.CustomerApproved : RideCancellationStatus.CustomerRejected;
        request.UpdatedAtUtc = DateTime.UtcNow;
        if (approve)
        {
            var waiting = new RideWaitingCoordinator(db);
            var stop = await waiting.ReconcileAsync(ride, DateTime.UtcNow, token);
            if (stop?.IsOpen == true) waiting.Close(ride, stop, DateTime.UtcNow, "ride_cancelled");
            var paid = await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid, token);
            var pendingPayment = await db.PaymentWalletOperations.AnyAsync(x => x.RideId == ride.Id && x.IsOpen && x.Status != "Succeeded", token);
            if (paid)
            {
                var quote = await RideCancellationQuote.GetAsync(db, ride, token, request);
                request.RequestedRefundAmount = quote.RefundAmount;
                request.CancellationLatitude = quote.Latitude;
                request.CancellationLongitude = quote.Longitude;
                request.LocationObservedAtUtc = quote.ObservedAtUtc;
            }
            request.Status = paid || pendingPayment ? RideCancellationStatus.AdminReviewPending : RideCancellationStatus.CustomerApproved;
            ride.Status = paid || pendingPayment ? RideStatus.CancellationPending : RideStatus.Cancelled;
            await RideRouteChangePolicy.ExpireAsync(db, ride, DateTime.UtcNow, token);
            if (!paid && !pendingPayment) await RideDriverAvailability.ReleaseAsync(db, ride, token);
            if (paid || pendingPayment)
                await NotifyAdminsAsync(ride, request, "وافق الراكب على طلب السائق؛ راجع الإلغاء والاسترداد المالي.", token);
        }
        else
        {
            request.Status = RideCancellationStatus.AdminReviewPending;
            ride.Status = RideStatus.CancellationPending;
            await NotifyAdminsAsync(ride, request, "رفض الراكب طلب إلغاء السائق؛ راجع الطلب واتخذ القرار.", token);
        }
        ride.UpdatedAtUtc = DateTime.UtcNow;
        foreach (var recipient in new[] { request.CustomerId, request.DriverId!.Value })
            db.Notifications.Add(new Notification
            {
                UserId = recipient, Type = NotificationType.RideStatus,
                Title = ride.Status == RideStatus.CancellationPending ? "طلب الإلغاء قيد مراجعة الإدارة" : "تم إلغاء الرحلة",
                Body = !approve ? "رفض الراكب طلب الإلغاء؛ أُحيل الطلب إلى الإدارة لاتخاذ القرار." : ride.Status == RideStatus.CancellationPending
                    ? "وافق الراكب على طلب الإلغاء؛ أُحيل الطلب إلى الإدارة لمراجعة الاسترداد المالي."
                    : "وافق الراكب على طلب الإلغاء؛ تم إلغاء الرحلة.",
                DataJson = $"{{\"action\":\"open_active_ride\",\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}"
            });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher != null) await realtimePublisher.PublishRideStatusUpdatedAsync(RideRealtimeEventFactory.Status(ride), token);
        return await ToResultAsync(request, token);
    }

    private async Task LockRequestRideAsync(int? requestId, CancellationToken token)
    {
        var rideId = await db.RideCancellationRequests.AsNoTracking()
            .Where(x => x.Id == requestId).Select(x => (int?)x.RideId).SingleOrDefaultAsync(token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        await RidePaymentGuard.LockAsync(db, rideId, token);
    }

    private async Task NotifyRefundMethodAsync(YemenDrive.Database.Entities.RideCancellationRequest request, CancellationToken token)
    {
        db.Notifications.Add(new Notification { UserId = request.DriverId!.Value, Type = NotificationType.Payment,
            Title = "اختر طريقة إعادة المبلغ", Body = "اكتملت موافقات الإلغاء. راجع صافي المبلغ واختر المحفظة أو أكد إعادته نقداً.",
            DataJson = $"{{\"action\":\"review_cancellation_request\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        await Task.CompletedTask;
    }

    protected override async Task<object?> UpdateAsync(RideCancellationRequestModel model, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockRequestRideAsync(model.Id, token);
        var request = await db.RideCancellationRequests.Include(x => x.Ride).SingleAsync(x => x.Id == model.Id, token);
        if (currentUser.UserId != request.DriverId) throw new ServiceException("cancellation_driver_access_denied", "اختيار طريقة الاسترداد متاح لسائق الرحلة فقط.");
        if (request.Status != RideCancellationStatus.RefundMethodPending) return await ToResultAsync(request, token);
        if (model.RefundChoiceRevision != request.RefundChoiceRevision)
            throw new ServiceException("refund_choice_changed", "تغيرت حالة الاسترداد؛ حدّث الطلب قبل اتخاذ القرار.");
        if (model.RequestedRefundMethod is not (CashCancellationRefundMethod.CreditCustomerWallet or CashCancellationRefundMethod.ReturnFromDriver))
            throw new ServiceException("cash_refund_method_required", "حدد النقد أو محفظة الراكب لإعادة المبلغ.");
        if (model.RequestedRefundMethod == CashCancellationRefundMethod.ReturnFromDriver && !model.CashReturned)
            throw new ServiceException("cash_return_confirmation_required", "أكد أنك أعدت صافي المبلغ نقداً إلى الراكب قبل تسجيل التسوية.");
        await EnsureWalletPaymentSettledAsync(request.RideId, token);
        request.RequestedRefundMethod = model.RequestedRefundMethod;
        request.RefundChoiceRevision++;
        request.UpdatedAtUtc = DateTime.UtcNow;
        if (request.RequestedRefundMethod == CashCancellationRefundMethod.CreditCustomerWallet)
        {
            request.Status = RideCancellationStatus.RefundConsentPending;
            db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.Payment,
                Title = "الموافقة على إعادة المبلغ للمحفظة", Body = $"اختار السائق إعادة {request.RequestedRefundAmount:0.##} إلى محفظتك. وافق على الطريقة أو ارفضها لطلب اختيار جديد.",
                DataJson = $"{{\"action\":\"review_cancellation_request\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        }
        else await FinalizeCashRefundAsync(request, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        await PublishCancellationAsync(request, token);
        return await ToResultAsync(request, token);
    }

    private async Task<object?> RefundConsentAsync(RideCancellationRequestModel model, bool approve, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockRequestRideAsync(model.Id, token);
        var request = await db.RideCancellationRequests.Include(x => x.Ride).SingleAsync(x => x.Id == model.Id, token);
        if (currentUser.UserId != request.CustomerId) throw new ServiceException("cancellation_customer_access_denied", "موافقة الاسترداد متاحة لراكب الرحلة فقط.");
        if (request.Status != RideCancellationStatus.RefundConsentPending) return await ToResultAsync(request, token);
        if (model.RefundChoiceRevision != request.RefundChoiceRevision)
            throw new ServiceException("refund_choice_changed", "تغيرت طريقة الاسترداد؛ حدّث الطلب قبل الموافقة.");
        request.UpdatedAtUtc = DateTime.UtcNow;
        if (approve)
        {
            request.RefundConsentedAtUtc = DateTime.UtcNow;
            await EnsureWalletPaymentSettledAsync(request.RideId, token);
            await FinalizeCashRefundAsync(request, token);
        }
        else
        {
            request.Status = RideCancellationStatus.DriverReviewPending;
            request.DriverDecision = RideCancellationDriverDecision.None;
            request.DriverDecidedAtUtc = null;
            db.Notifications.Add(new Notification { UserId = request.DriverId!.Value, Type = NotificationType.Payment,
                Title = "رفض الراكب الاسترداد إلى المحفظة", Body = "راجع طلب الإلغاء مجدداً ثم اختر طريقة إعادة المبلغ. لم تُسجّل أي تسوية مالية.",
                DataJson = $"{{\"action\":\"review_cancellation_request\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        await PublishCancellationAsync(request, token);
        return await ToResultAsync(request, token);
    }

    private async Task FinalizeCashRefundAsync(YemenDrive.Database.Entities.RideCancellationRequest request, CancellationToken token)
    {
        if (request.AdminDecidedAtUtc is null || request.AdminUserId is null)
            throw new ServiceException("admin_approval_required", "يجب استكمال موافقة الإدارة قبل تسوية الاسترداد.");
        var settlement = await SettlePaidCancellationAsync(request, request.AdminUserId.Value, token)
            ?? throw new ServiceException("payment_not_found", "الدفعة الأصلية غير موجودة.");
        request.Status = RideCancellationStatus.AdminApproved;
        request.Ride.Status = RideStatus.Cancelled;
        request.Ride.UpdatedAtUtc = DateTime.UtcNow;
        await RideDriverAvailability.ReleaseAsync(db, request.Ride, token);
        foreach (var recipient in new[] { request.CustomerId, request.DriverId!.Value })
            db.Notifications.Add(new Notification { UserId = recipient, Type = NotificationType.Payment,
                Title = "اكتمل الإلغاء والاسترداد", Body = settlement.CreditedToWallet
                    ? $"أُعيد {settlement.RefundAmount:0.##} {settlement.Currency} إلى محفظة الراكب بعد الخصومات."
                    : $"أكد السائق إعادة {settlement.RefundAmount:0.##} {settlement.Currency} نقداً إلى الراكب بعد الخصومات.",
                DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
    }

    private async Task PublishCancellationAsync(YemenDrive.Database.Entities.RideCancellationRequest request, CancellationToken token)
    {
        if (realtimePublisher is null) return;
        await realtimePublisher.PublishRideStatusUpdatedAsync(RideRealtimeEventFactory.Status(request.Ride), token);
        await realtimePublisher.PublishPaymentUpdatedAsync(new[] { request.CustomerId, request.DriverId!.Value },
            new PaymentRealtimeEvent(request.RideId, "rideCancellationUpdated", DateTime.UtcNow), token);
    }

    private async Task EnsureWalletPaymentSettledAsync(int rideId, CancellationToken token)
    {
        if (await db.PaymentWalletOperations.AnyAsync(x => x.RideId == rideId && x.IsOpen && x.Status != "Succeeded", token))
            throw new ServiceException("wallet_payment_in_progress", "يجب إكمال عملية دفع المحفظة أو التحقق من نتيجتها قبل إلغاء الرحلة.");
    }

    private sealed record CancellationSettlementResult(
        decimal RefundAmount,
        string Currency,
        CashCancellationRefundMethod? RefundMethod,
        bool CreditedToWallet,
        bool DriverDebtCreated,
        decimal DriverDebtAmount = 0m);
}
