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
                    x.Status == RideCancellationStatus.RefundMethodPending || x.Status == RideCancellationStatus.RefundConsentPending || x.Status == RideCancellationStatus.CashReceiptPending || x.Status == RideCancellationStatus.FinancialReviewPending), token))
            throw new ServiceException("ride_cancellation_already_open", "هذه الرحلة لديها طلب إلغاء قيد المراجعة.");

        if (customerId == ride.DriverId)
        {
            var driverRequest = await AddByDriverAsync(ride, reason, model.SafeStopConfirmed, token);
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
        var directBeforeArrivalCancellation = !isInProgress && !hasPaidPayment && !paymentPending &&
            (ride.Status is RideStatus.DriverAssigned or RideStatus.DriverEnRoute ||
             ride.DriverId is null && ride.Status is RideStatus.Searching or RideStatus.Negotiating);
        var reviewByAdmin = ride.DriverId is null;
        if (isInProgress && !model.SafeStopConfirmed)
            throw new ServiceException("safe_stop_confirmation_required", "اتفق على نقطة آمنة وأكد توقف النقل فعلياً قبل إرسال إلغاء الرحلة. يمكنك فتح بلاغ السلامة عند الحاجة.");
        var request = new YemenDrive.Database.Entities.RideCancellationRequest
        {
            RideId = ride.Id,
            CustomerId = customerId,
            DriverId = ride.DriverId,
            RideStatusAtRequest = ride.Status,
            Status = directBeforeArrivalCancellation ? RideCancellationStatus.AdminApproved : reviewByAdmin ? RideCancellationStatus.AdminReviewPending : RideCancellationStatus.DriverReviewPending,
            DriverDecision = directBeforeArrivalCancellation && ride.DriverId is not null ? RideCancellationDriverDecision.Accepted : RideCancellationDriverDecision.None,
            Reason = reason,
            RequestedRefundMethod = model.RequestedRefundMethod
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
            request.TravelledFareSnapshot = quote.TravelledFare;
            if (quote.ObservedAtUtc is not null)
            {
                request.CancellationLatitude = quote.Latitude;
                request.CancellationLongitude = quote.Longitude;
                request.LocationObservedAtUtc = quote.ObservedAtUtc;
            }
        }
        var searchRecipients = new List<int>();
        var searchOffers = new List<YemenDrive.Database.Entities.RideOffer>();
        if (ride.DriverId is null)
        {
            // The generic cancellation endpoint must close the same pending
            // invitations and offers as the search-specific cancellation path.
            searchOffers = await db.RideOffers.Where(x => x.RideId == ride.Id && x.Status == OfferStatus.Pending).ToListAsync(token);
            foreach (var offer in searchOffers)
            {
                offer.Status = OfferStatus.Expired;
                offer.UpdatedAtUtc = DateTime.UtcNow;
            }
            var inviteData = RideSearchNotificationDispatcher.RideData(ride.Id);
            searchRecipients = await db.Notifications.AsNoTracking()
                .Where(x => x.Type == NotificationType.RideOffer && x.DataJson == inviteData)
                .Select(x => x.UserId).Distinct().ToListAsync(token);
            searchRecipients = searchRecipients.Concat(searchOffers.Select(x => x.DriverId)).Distinct().ToList();
            db.Notifications.AddRange(searchRecipients.Select(driverId => new Notification
            {
                UserId = driverId, Type = NotificationType.RideStatus, Title = "ألغى العميل البحث",
                Body = "ألغى العميل هذه الرحلة؛ لا يمكنك إرسال عرض عليها.",
                DataJson = $"{{\"action\":\"open_notifications\",\"rideId\":{ride.Id},\"status\":\"Cancelled\"}}"
            }));
        }
        await RideFinancialState.CloseServiceAsync(db, ride, reason, token);
        ride.Status = RideStatus.Cancelled;
        if (!isInProgress && !hasPaidPayment && !paymentPending) ride.FinancialAmountDue = 0m;
        ride.UpdatedAtUtc = DateTime.UtcNow;
        if (directBeforeArrivalCancellation) await RideDriverAvailability.ReleaseAsync(db, ride, token);
        if (!directBeforeArrivalCancellation)
        {
            request.AssignedAdminId = await OperationalReviewCoordinator.OwnerAsync(db, "refunds.write", token);
            if (ride.DecisionResponseMinutes != null) request.ReviewDueAtUtc = request.CreatedAtUtc.AddMinutes(ride.DecisionResponseMinutes.Value);
        }
        db.RideCancellationRequests.Add(request);
        await db.SaveChangesAsync(token);
        if (directBeforeArrivalCancellation)
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
        {
            if (ride.DriverId is null)
            {
                await realtimePublisher.PublishRideUnavailableAsync(ride.Id, ride.CustomerId, searchRecipients, token);
                var expired = await RideRealtimeEventFactory.LoadOffersAsync(db, searchOffers.Select(x => x.Id), token);
                await realtimePublisher.PublishOfferUpdatesAsync(ride.CustomerId, expired, token);
            }
            await realtimePublisher.PublishRideStatusUpdatedAsync(
                RideRealtimeEventFactory.Status(ride), token);
        }
        return await ToResultAsync(request, token);
    }

    protected override async Task<object?> GetAsync(RideCancellationRequestModel model, CancellationToken token)
    {
        if (!model.PendingOnly && model.Id is null && model.RideId is null) throw new ServiceException("id_required", "معرف طلب الإلغاء أو الرحلة مطلوب.");
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        if (model.PendingOnly)
        {
            var pending = await db.RideCancellationRequests.AsNoTracking().Include(x => x.Ride)
                .Where(x => x.CustomerId == userId &&
                    (x.Status == RideCancellationStatus.DriverReviewPending || x.Status == RideCancellationStatus.AdminReviewPending ||
                     x.Status == RideCancellationStatus.CustomerReviewPending || x.Status == RideCancellationStatus.RefundMethodPending ||
                     x.Status == RideCancellationStatus.RefundConsentPending || x.Status == RideCancellationStatus.CashReceiptPending ||
                     x.Status == RideCancellationStatus.FinancialReviewPending))
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
            return pending is null ? null : await ToResultAsync(pending, token);
        }
        var isAdmin = await IsAdminAsync(userId, token);
        if (model.PreviewOnly)
        {
            var previewRequest = model.Id is int previewId ? await db.RideCancellationRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == previewId, token) : null;
            if (model.Id != null && previewRequest == null) throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
            var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == (previewRequest == null ? model.RideId : previewRequest.RideId), token)
                ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
            if (!isAdmin && ride.CustomerId != userId && ride.DriverId != userId)
                throw new ServiceException("ride_access_denied", "لا تملك صلاحية مراجعة استرداد هذه الرحلة.");
            if (isAdmin && previewRequest != null && model.WaiveCancellationFees != null) previewRequest.WaiveCancellationFees = model.WaiveCancellationFees.Value;
            return await RideCancellationQuote.GetAsync(db, ride, token, previewRequest,
                reviewedTravelledFare: isAdmin ? model.ReviewedTravelledFare : null);
        }
        var query = db.RideCancellationRequests.AsNoTracking().Include(x => x.Ride).AsQueryable();
        if (model.Id is int id) query = query.Where(x => x.Id == id);
        else query = query.Where(x => x.RideId == model.RideId);
        var request = await query.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(token);
        if (request is null)
        {
            // The clients also read this optional snapshot while tracking a
            // normal ride. Absence is not a failed ride refresh, but the ride
            // must still belong to the caller before returning an empty result.
            if (model.Id is not null)
                throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
            var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.RideId, token)
                ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
            if (!isAdmin && ride.CustomerId != userId && ride.DriverId != userId)
                throw new ServiceException("ride_access_denied", "لا تملك صلاحية الوصول إلى هذه الرحلة.");
            return null;
        }
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
            request.Status = RideCancellationStatus.AdminReviewPending;
            await NotifyAdminsAsync(request.Ride, request, "رفض السائق طلب الإلغاء؛ أول خلاف يُحال إلى الإدارة. انتهى النقل وتبقى الحقوق المالية للمراجعة.", token);
            db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "أُحيل الخلاف إلى الإدارة", Body = "انتهى النقل؛ أُحيل سبب الإلغاء وحقوق الطرفين المالية للمراجعة." });
        }
        else if (decision == RideCancellationDriverDecision.Accepted)
        {
            var hasPaidPayment = await db.PaymentTransactions.AnyAsync(
                x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid, token);
            var paymentPending = await db.PaymentWalletOperations.AnyAsync(x => x.RideId == request.RideId && x.IsOpen && x.Status != "Succeeded", token);
            if (!hasPaidPayment && !paymentPending && request.RideStatusAtRequest != RideStatus.InProgress)
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
        await YemenDrive.Application.Administration.AdministratorAccess.RequireAsync(db, adminId, "refunds.write", token);
        if (!await IsAdminAsync(adminId, token)) throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockRequestRideAsync(model.Id, token);
        var request = await db.RideCancellationRequests.Include(x => x.Ride).SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        var customerApprovedRefund = request.Status == RideCancellationStatus.CustomerApproved && request.AdminDecidedAtUtc == null &&
            await db.PaymentTransactions.AnyAsync(x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid, token);
        if (request.Status is not (RideCancellationStatus.AdminReviewPending or RideCancellationStatus.FinancialReviewPending) && !customerApprovedRefund) throw new ServiceException("cancellation_decision_closed", "لم يعد هذا القرار متاحاً؛ حدّث الطلب.");
        if (approve) await EnsureWalletPaymentSettledAsync(request.RideId, token);
        var previousRideStatus = request.Ride.Status;
        if (!customerApprovedRefund) request.Status = approve ? RideCancellationStatus.AdminApproved : RideCancellationStatus.AdminRejected;
        request.AssignedAdminId = adminId;
        request.AdminUserId = adminId;
        request.AdminDecidedAtUtc = DateTime.UtcNow;
        request.AdminNote = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim();
        if (string.IsNullOrWhiteSpace(request.AdminNote)) throw new ServiceException("decision_reason_required", "اذكر أساس القرار والمسؤولية المالية.");
        if (model.Responsibility != null && model.Responsibility is not ("Customer" or "Driver" or "Company" or "Shared" or "Undetermined"))
            throw new ServiceException("invalid_responsibility", "حدد الطرف المسؤول وفق الأدلة.");
        request.Responsibility = model.Responsibility ?? request.Responsibility;
        if (request.Responsibility is "Driver" or "Company" && model.WaiveCancellationFees != true)
            throw new ServiceException("fault_fees_require_waiver", "لا يتحمل الراكب رسوم إلغاء ناجم عن السائق أو الشركة؛ اعتمد إعفاء الرسوم.");
        request.WaiveCancellationFees = model.WaiveCancellationFees ?? request.WaiveCancellationFees;
        if (model.CashReceiptConfirmed)
        {
            if (request.CashReturnedAtUtc == null || string.IsNullOrWhiteSpace(model.EvidenceReference))
                throw new ServiceException("cash_refund_evidence_required", "تحقق من دليل استلام النقد وسجل مرجعه؛ لا يكفي قول السائق.");
            request.CustomerCashReceiptConfirmed = true;
            request.RequestedRefundMethod = CashCancellationRefundMethod.ReturnFromDriver;
            db.OperationalDecisionAudits.Add(new() { ActorUserId = adminId, Subject = "CashRefundEvidence", SubjectId = request.Id, Action = "verified_receipt", Reason = model.EvidenceReference });
        }
        db.OperationalDecisionAudits.Add(new() { ActorUserId = adminId, Subject = "RideCancellation", SubjectId = request.Id, Action = approve ? "approve" : "reject", Reason = request.AdminNote });
        CancellationSettlementResult? settlement = null;
        if (approve)
        {
            // A paid cancellation is intentionally settled only at this
            // administrative decision point.  The original payment remains
            // immutable; this creates its own refund/payment record, wallet
            // movement when applicable, driver receivable and reversing
            // journal entry in the same serializable transaction.
            var quoteForDecision = await RideCancellationQuote.GetAsync(db, request.Ride, token, request, model.ReviewedTravelledFare);
            if (quoteForDecision.TotalDeductions is null) throw new ServiceException("cancellation_refund_review_required", quoteForDecision.CalculationMessage ?? "راجع الأجرة المقطوعة.");
            request.TravelledFareSnapshot = quoteForDecision.TravelledFare;
            request.Ride.FinancialAmountDue = quoteForDecision.TotalDeductions;
            request.RequestedRefundAmount = quoteForDecision.RefundAmount;
            var originalFareForDecision = Math.Max(0m, (request.Ride.CustomerPrice ?? request.Ride.ServerPrice ?? 0m) + request.Ride.WaitingFee);
            request.Ride.FinancialDriverEntitlement = originalFareForDecision <= 0 ? 0m : decimal.Round(Math.Max(0m, request.Ride.DriverShare ?? 0m) * Math.Min(originalFareForDecision, (quoteForDecision.TravelledFare ?? 0m) + quoteForDecision.WaitingFee) / originalFareForDecision, 2);
            if (model.FundingLedgerAccountId is int payoutAccount)
            {
                if (model.VerifiedPayoutAmount is not decimal payoutAmount || payoutAmount != request.RequestedRefundAmount) throw new ServiceException("refund_amount_changed", "المبلغ المحول لا يطابق صافي الاسترداد؛ راجع الفاتورة.");
                await VerifiedRefundPayout.ValidateAsync(db, adminId, payoutAccount, payoutAmount, model.EvidenceReference, token);
                request.RefundFundingLedgerAccountId = payoutAccount; request.RefundPayoutEvidenceReference = model.EvidenceReference;
                request.RefundDestination = "VerifiedAlternative";
                db.OperationalDecisionAudits.Add(new() { ActorUserId = adminId, Subject = "RefundPayoutEvidence", SubjectId = request.Id, Action = "verified_transfer", Reason = model.EvidenceReference! });
            }
            var externalPaid = await db.PaymentTransactions.AnyAsync(x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid && (x.Provider == "Floosak" || x.Provider == "Jaib"), token);
            var cashPaid = await db.PaymentTransactions.AnyAsync(x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid && x.Provider == "Cash", token);
            if (request.RefundFundingLedgerAccountId == null && (request.RequestedRefundAmount > 0 && cashPaid && request.CustomerCashReceiptConfirmed != true && request.RefundConsentedAtUtc == null || externalPaid && request.RefundConsentedAtUtc == null && request.RequestedRefundAmount > 0))
            {
                var quote = await RideCancellationQuote.GetAsync(db, request.Ride, token, request, model.ReviewedTravelledFare);
                if (quote.RefundAmount is null) throw new ServiceException("cancellation_refund_review_required", quote.CalculationMessage ?? "راجع أجرة الجزء المقطوع قبل الاعتماد.");
                request.RequestedRefundAmount = quote.RefundAmount;
            request.TravelledFareSnapshot = quote.TravelledFare;
                request.Status = cashPaid ? RideCancellationStatus.RefundMethodPending : RideCancellationStatus.RefundConsentPending;
                request.RefundDestination = "YemenDriveWallet";
                request.RequestedRefundMethod = CashCancellationRefundMethod.CreditCustomerWallet;
                if (cashPaid) await NotifyRefundMethodAsync(request, token);
                else db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.Payment,
                    Title = "اختر وجهة الاسترداد", Body = "استرداد المصدر الخارجي غير موثق في الربط الحالي. يمكنك قبول محفظة التطبيق أو رفضها لفتح معالجة مالية مع الإدارة.",
                    DataJson = $"{{\"action\":\"review_cancellation_request\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
            else
            {
                settlement = await SettlePaidCancellationAsync(request, adminId, token, model.ReviewedTravelledFare);
                request.Ride.Status = RideStatus.Cancelled;
                request.Ride.UpdatedAtUtc = DateTime.UtcNow;
                await RideDriverAvailability.ReleaseAsync(db, request.Ride, token);
            }
        }
        if (!approve)
        {
            request.Status = RideCancellationStatus.FinancialReviewPending;
            await NotifyAdminsAsync(request.Ride, request, "رفضت الإدارة التسوية المقترحة. النقل منتهٍ؛ تبقى الحالة المالية مفتوحة لمراجعة المسؤولية والأدلة.", token);
        }
        if (approve && request.Status == RideCancellationStatus.AdminApproved) await RideFinancialState.SynchronizeAsync(db, request.Ride, adminId, token);
        var customerBody = !approve
            ? "راجعت الإدارة الطلب ولم توافق على الإلغاء المالي."
            : request.Status == RideCancellationStatus.RefundMethodPending
                ? "وافقت الإدارة على الإلغاء. انتهى النقل؛ يحدد مسار إعادة صافي المبلغ وتأكيد استلامه."
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
        var ride = request.Ride;
        var receipts = await db.PaymentTransactions.Where(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid).OrderByDescending(x => x.Id).ToListAsync(token);
        if (receipts.Count == 0) return null;
        if (ride.DriverId == null) throw new ServiceException("driver_not_assigned", "السائق المسند غير موجود.");
        var quote = await RideCancellationQuote.GetAsync(db, ride, token, request, reviewedTravelledFare);
        var money = await RideFinancialState.ReadAsync(db, ride, token);
        var refundAmount = Math.Min(quote.RefundAmount ?? throw new ServiceException("cancellation_refund_review_required", "راجع الأجرة المقطوعة أولاً."), money.RefundDue);
        var cash = receipts.Any(x => x.Provider == "Cash");
        var external = receipts.Any(x => x.Provider is "Floosak" or "Jaib");
        var verifiedPayout = request.RefundFundingLedgerAccountId != null;
        var walletRefund = !verifiedPayout && (!cash || request.RequestedRefundMethod == CashCancellationRefundMethod.CreditCustomerWallet);
        if (!verifiedPayout && refundAmount > 0 && (external || cash && walletRefund) && request.RefundConsentedAtUtc == null)
            throw new ServiceException("refund_consent_required", "يلزم قبول الراكب وجهة محفظة التطبيق؛ لم يثبت دعم استرداد المصدر الخارجي.");
        if (!verifiedPayout && refundAmount > 0 && cash && !walletRefund && request.CustomerCashReceiptConfirmed != true)
            throw new ServiceException("cash_receipt_not_confirmed", "استلام النقد بحاجة إلى تأكيد الراكب أو قرار موثق.");
        if (!verifiedPayout && cash && !walletRefund && receipts.Any(x => x.Provider != "Cash"))
            throw new ServiceException("mixed_refund_requires_review", "دفعات الرحلة مختلطة؛ راجع توزيع وجهة الاسترداد مع الإدارة.");
        var reference = $"cancellation:{request.Id}:refund";
        if (await db.JournalEntries.AnyAsync(x => x.SourceType == "RideCancellation" && x.SourceId == reference, token))
            return new(refundAmount, receipts[0].Currency, request.RequestedRefundMethod, walletRefund, false);
        await RideFinancialState.SnapshotEntitlementAsync(db, ride, token);
        var driverReversal = Math.Max(0m, ride.CollectedDriverEntitlement!.Value - RideFinancialState.DriverEntitlement(ride));
        var driverWallet = await db.Wallets.SingleAsync(x => x.UserId == ride.DriverId, token);
        var customerWallet = await db.Wallets.SingleAsync(x => x.UserId == ride.CustomerId, token);
        var electronicReversal = await RideFinancialState.AllocateDriverReversalAsync(db, ride, driverReversal, token);
        var walletDebit = Math.Min(Math.Max(0m, driverWallet.Balance), electronicReversal);
        driverWallet.Balance -= walletDebit;
        if (walletDebit > 0) db.WalletTransactions.Add(new() { WalletId = driverWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Debit, Amount = walletDebit, BalanceAfter = driverWallet.Balance, ExternalReference = reference, Description = "عكس الجزء غير المكتسب من مستحقات الرحلة الملغاة" });
        if (walletRefund && refundAmount > 0)
        {
            if (customerWallet.Currency != receipts[0].Currency) throw new ServiceException("currency_mismatch", "عملة الاسترداد لا تطابق محفظة العميل.");
            customerWallet.Balance += refundAmount;
            db.WalletTransactions.Add(new() { WalletId = customerWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Refund, Amount = refundAmount, BalanceAfter = customerWallet.Balance, ExternalReference = reference, Description = "استرداد إلغاء الرحلة بعد مراجعة الحقوق وبموافقة العميل" });
        }
        if (refundAmount > 0) db.PaymentTransactions.Add(new() { UserId = ride.CustomerId, RideId = ride.Id, Amount = refundAmount, Currency = receipts[0].Currency, Provider = verifiedPayout ? "VerifiedCancellationPayout" : walletRefund ? "CancellationRefundToWallet" : "CashReturnFromDriver", Status = PaymentStatus.Refunded, ProviderReference = reference });
        var basis = await RideFinancialState.RefundAccountingBasisAsync(db, ride, token);
        await accounting.PostCancellationAsync(basis, receipts[0], refundAmount, walletRefund, driverReversal, actorUserId, token, driverWalletDebit: walletDebit, sourceReference: reference, refundFundingAccountId: request.RefundFundingLedgerAccountId,
            retainedServiceFee: Math.Min(quote.ServiceFee, Math.Max(0m, money.PaidAmount - money.RefundedAmount - refundAmount)));
        var currentAccountDelta = driverReversal - walletDebit - (walletRefund || verifiedPayout ? 0m : refundAmount);
        if (currentAccountDelta != 0) db.DriverSettlements.Add(new() { DriverId = ride.DriverId.Value, PeriodStartUtc = DateTime.UtcNow, PeriodEndUtc = DateTime.UtcNow, Adjustments = -currentAccountDelta, NetPayable = -currentAccountDelta, Status = currentAccountDelta > 0 ? PaymentStatus.Pending : PaymentStatus.Refunded, PaymentReference = reference + ":driver-current" });
        ride.CollectedDriverEntitlement -= driverReversal;
        await db.SaveChangesAsync(token);
        await RideFinancialState.SynchronizeAsync(db, ride, actorUserId, token);
        return new(refundAmount, receipts[0].Currency, request.RequestedRefundMethod, walletRefund, currentAccountDelta > 0, Math.Max(0m, currentAccountDelta));
    }

    private async Task NotifyAdminsAsync(YemenDrive.Database.Entities.Ride ride, YemenDrive.Database.Entities.RideCancellationRequest request, string body, CancellationToken token)
    {
        var adminIds = await db.Users.AsNoTracking().Where(x => x.Role == UserRole.Admin && x.IsActive && (x.IsSystemAdministrator || db.AdministratorPermissions.Any(p => p.UserId == x.Id && (p.Permission == "*" || p.Permission == "refunds.read" || p.Permission == "refunds.write")))).Select(x => x.Id).ToListAsync(token);
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
        cashReceiptPending = x.Status == RideCancellationStatus.CashReceiptPending,
        x.Ride.ServiceEndedAtUtc, x.Responsibility, x.WaiveCancellationFees, x.CustomerCashReceiptConfirmed,
        refundDestination = x.RefundDestination, x.RefundPayoutEvidenceReference, x.RefundFundingLedgerAccountId,
        financialReviewPending = x.Status == RideCancellationStatus.FinancialReviewPending,
        x.RefundChoiceRevision,
        canDecide = x.Status is RideCancellationStatus.CustomerReviewPending or RideCancellationStatus.RefundConsentPending or RideCancellationStatus.CashReceiptPending ? currentUser.UserId == x.CustomerId :
            x.Status == RideCancellationStatus.DriverReviewPending && currentUser.UserId == x.DriverId,
        rideStatus = x.Ride.Status,
        x.Reason, x.RequestedRefundMethod, x.RequestedRefundAmount, x.AssignedAdminId, x.ReviewDueAtUtc, x.CancellationLatitude, x.CancellationLongitude,
        x.LocationObservedAtUtc, x.DriverDecidedAtUtc, x.DriverNote, x.AdminUserId, x.AdminDecidedAtUtc, x.AdminNote, x.CreatedAtUtc,
        refundQuote = await RideCancellationQuote.GetAsync(db, x.Ride, token, x)
      };
    }

    private static bool IsDriverRequest(YemenDrive.Database.Entities.RideCancellationRequest request) =>
        request.RequestedByDriver || request.Status is RideCancellationStatus.CustomerReviewPending or RideCancellationStatus.CustomerApproved or
            RideCancellationStatus.CustomerRejected or RideCancellationStatus.DriverCancelled;

    private async Task<YemenDrive.Database.Entities.RideCancellationRequest> AddByDriverAsync(
        YemenDrive.Database.Entities.Ride ride, string reason, bool safeStopConfirmed, CancellationToken token)
    {
        var pending = ride.Status == RideStatus.InProgress;
        if (pending && !safeStopConfirmed) throw new ServiceException("safe_stop_confirmation_required", "أكد انتهاء النقل عند نقطة آمنة قبل طلب الإلغاء.");
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
            RequestedRefundMethod = null
        };
        var waiting = new RideWaitingCoordinator(db);
        var stop = await waiting.ReconcileAsync(ride, DateTime.UtcNow, token);
        if (stop?.IsOpen == true) waiting.Close(ride, stop, DateTime.UtcNow, "ride_cancelled");
        if (paid)
        {
            var quote = await RideCancellationQuote.GetAsync(db, ride, token, request);
            request.RequestedRefundAmount = quote.RefundAmount;
            request.TravelledFareSnapshot = quote.TravelledFare;
            request.CancellationLatitude = quote.Latitude;
            request.CancellationLongitude = quote.Longitude;
            request.LocationObservedAtUtc = quote.ObservedAtUtc;
        }
        await RideFinancialState.CloseServiceAsync(db, ride, reason, token);
        ride.Status = RideStatus.Cancelled;
        if (!pending) ride.FinancialAmountDue = 0m;
        request.AssignedAdminId = await OperationalReviewCoordinator.OwnerAsync(db, "refunds.write", token);
        if (ride.DecisionResponseMinutes != null) request.ReviewDueAtUtc = request.CreatedAtUtc.AddMinutes(ride.DecisionResponseMinutes.Value);
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
            Body = pending ? "يرجى مراجعة سبب الإلغاء. انتهى النقل؛ تراجع الموافقات الحقوق المالية بصورة مستقلة." : "ألغى السائق الرحلة قبل بدءها.",
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
        return request.Status is RideCancellationStatus.RefundConsentPending or RideCancellationStatus.CashReceiptPending ? await RefundConsentAsync(model, approve, token) :
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
        if (ride.Status is not (RideStatus.InProgress or RideStatus.DriverAssigned or RideStatus.DriverEnRoute or RideStatus.DriverArrived or RideStatus.Cancelled))
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
            request.TravelledFareSnapshot = quote.TravelledFare;
                request.CancellationLatitude = quote.Latitude;
                request.CancellationLongitude = quote.Longitude;
                request.LocationObservedAtUtc = quote.ObservedAtUtc;
            }
            var needsFinancialReview = paid || pendingPayment || request.RideStatusAtRequest == RideStatus.InProgress;
            request.Status = needsFinancialReview ? RideCancellationStatus.AdminReviewPending : RideCancellationStatus.CustomerApproved;
            ride.Status = RideStatus.Cancelled;
            await RideRouteChangePolicy.ExpireAsync(db, ride, DateTime.UtcNow, token);
            if (!paid && !pendingPayment) await RideDriverAvailability.ReleaseAsync(db, ride, token);
            if (paid || pendingPayment)
                await NotifyAdminsAsync(ride, request, "وافق الراكب على طلب السائق؛ راجع الإلغاء والاسترداد المالي.", token);
        }
        else
        {
            request.Status = RideCancellationStatus.AdminReviewPending;
            ride.Status = RideStatus.Cancelled;
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
                Title = "الموافقة على إعادة المبلغ للمحفظة", Body = $"اختار السائق إعادة {request.RequestedRefundAmount:0.##} إلى محفظتك. وافق على الطريقة أو ارفضها لفتح مراجعة مالية مع الإدارة.",
                DataJson = $"{{\"action\":\"review_cancellation_request\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        }
        else
        {
            request.CashReturnedAtUtc = DateTime.UtcNow;
            request.Status = RideCancellationStatus.CashReceiptPending;
            db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.Payment,
                Title = "هل استلمت المبلغ النقدي؟", Body = $"أفاد السائق بإعادة {request.RequestedRefundAmount:0.##} نقداً. أكد الاستلام أو اعترض؛ لم يسجل الاسترداد مكتملًا بعد.",
                DataJson = $"{{\"action\":\"review_cancellation_request\",\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        }
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
        if (request.Status is not (RideCancellationStatus.RefundConsentPending or RideCancellationStatus.CashReceiptPending)) throw new ServiceException("refund_decision_closed", "لم يعد قرار الاسترداد متاحاً؛ حدّث الطلب.");
        if (model.RefundChoiceRevision != request.RefundChoiceRevision)
            throw new ServiceException("refund_choice_changed", "تغيرت طريقة الاسترداد؛ حدّث الطلب قبل الموافقة.");
        request.UpdatedAtUtc = DateTime.UtcNow;
        if (approve)
        {
            request.RefundConsentedAtUtc = DateTime.UtcNow;
            if (request.Status == RideCancellationStatus.CashReceiptPending) request.CustomerCashReceiptConfirmed = true;
            await EnsureWalletPaymentSettledAsync(request.RideId, token);
            await FinalizeCashRefundAsync(request, token);
        }
        else
        {
            request.Status = RideCancellationStatus.FinancialReviewPending;
            await NotifyAdminsAsync(request.Ride, request, "رفض الراكب وجهة الاسترداد أو اعترض على استلام النقد. عالج حقه مالياً مع الأدلة؛ لا تعاد دورة الموافقات.", token);
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
                    : $"تأكد استلام الراكب {settlement.RefundAmount:0.##} {settlement.Currency} نقداً بعد الخصومات.",
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
