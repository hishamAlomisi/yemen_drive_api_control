using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
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
    RideAccountingPostingService accounting) : OperationsService<RideCancellationRequestModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["add", "get", "accept", "reject", "refer", "adminApprove", "adminReject"];

    protected override async Task<object?> AddAsync(RideCancellationRequestModel model, CancellationToken token)
    {
        var customerId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كعميل.");
        var reason = model.Reason?.Trim();
        if (model.RideId is null || string.IsNullOrWhiteSpace(reason) || reason.Length < 3)
            throw new ServiceException("cancellation_reason_required", "حدد سبب الإلغاء أو اكتبه بوضوح قبل إرسال الطلب.");
        if (reason.Length > 1000) throw new ServiceException("invalid_cancellation_reason", "سبب الإلغاء أطول من الحد المسموح.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.Id == model.RideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != customerId) throw new ServiceException("ride_access_denied", "لا تملك صلاحية إلغاء هذه الرحلة.");
        if (ride.Status == RideStatus.Completed)
            throw new ServiceException("completed_ride_not_cancellable", "لا يمكن إلغاء رحلة مكتملة أو طلب استرداد لها.");
        if (ride.Status == RideStatus.Cancelled)
            throw new ServiceException("ride_not_cancellable", "هذه الرحلة ملغاة بالفعل.");
        if (ride.Status == RideStatus.CancellationPending && await db.RideCancellationRequests.AnyAsync(
                x => x.RideId == ride.Id && (x.Status == RideCancellationStatus.DriverReviewPending || x.Status == RideCancellationStatus.AdminReviewPending), token))
            throw new ServiceException("ride_cancellation_already_open", "هذه الرحلة لديها طلب إلغاء قيد المراجعة.");

        var isInProgress = ride.Status == RideStatus.InProgress || ride.StartedAtUtc is not null;
        var hasPaidPayment = await db.PaymentTransactions.AnyAsync(
            x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid, token);
        var directEnRouteCancellation = ride.Status == RideStatus.DriverEnRoute && !hasPaidPayment;
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
            RequestedRefundMethod = model.RequestedRefundMethod
        };

        if (isInProgress)
        {
            var latestLocation = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == ride.Id)
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

        ride.Status = directEnRouteCancellation ? RideStatus.Cancelled : RideStatus.CancellationPending;
        ride.UpdatedAtUtc = DateTime.UtcNow;
        db.RideCancellationRequests.Add(request);
        await db.SaveChangesAsync(token);
        if (directEnRouteCancellation)
        {
            if (ride.DriverId is int directDriverId)
                db.Notifications.Add(new Notification { UserId = directDriverId, Type = NotificationType.RideStatus, Title = "تم إلغاء الرحلة", Body = "ألغى العميل الرحلة قبل بدءها. يمكنك إيقاف التوجه إلى نقطة الانطلاق.", DataJson = $"{{\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
            db.Notifications.Add(new Notification { UserId = customerId, Type = NotificationType.RideStatus, Title = "تم إلغاء الرحلة", Body = "تم إلغاء الرحلة بناءً على السبب الذي اخترته.", DataJson = $"{{\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
        }
        else if (reviewByAdmin)
        {
            await NotifyAdminsAsync(ride, request, isInProgress ? "بدأت الرحلة؛ أُحيل طلب الإلغاء مباشرةً إلى الإدارة." : "طلب الإلغاء يحتاج مراجعة الإدارة.", token);
            db.Notifications.Add(new Notification { UserId = customerId, Type = NotificationType.RideStatus, Title = "طلب الإلغاء قيد المراجعة", Body = "أُوقفت الرحلة وأُحيل طلبك إلى الإدارة. سيصلك إشعار بالقرار.", DataJson = $"{{\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
        }
        else
        {
            db.Notifications.Add(new Notification { UserId = ride.DriverId!.Value, Type = NotificationType.RideStatus, Title = "طلب إلغاء رحلة", Body = "طلب العميل إلغاء الرحلة. راجع السبب واختر القبول أو الرفض أو الإحالة للإدارة.", DataJson = $"{{\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
            db.Notifications.Add(new Notification { UserId = customerId, Type = NotificationType.RideStatus, Title = "أُوقفت الرحلة", Body = "أُرسل طلب الإلغاء إلى السائق للمراجعة.", DataJson = $"{{\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ToResult(request);
    }

    protected override async Task<object?> GetAsync(RideCancellationRequestModel model, CancellationToken token)
    {
        if (model.Id is null && model.RideId is null) throw new ServiceException("id_required", "معرف طلب الإلغاء أو الرحلة مطلوب.");
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        var isAdmin = await IsAdminAsync(userId, token);
        var query = db.RideCancellationRequests.AsNoTracking().Include(x => x.Ride).AsQueryable();
        if (model.Id is int id) query = query.Where(x => x.Id == id);
        else query = query.Where(x => x.RideId == model.RideId);
        var request = await query.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        if (!isAdmin && request.CustomerId != userId && request.DriverId != userId)
            throw new ServiceException("cancellation_request_access_denied", "لا تملك صلاحية الوصول إلى طلب الإلغاء.");
        return ToResult(request);
    }

    protected override Task<object?> AcceptAsync(RideCancellationRequestModel model, CancellationToken token) => DriverDecideAsync(model, RideCancellationDriverDecision.Accepted, token);
    protected override Task<object?> RejectAsync(RideCancellationRequestModel model, CancellationToken token) => DriverDecideAsync(model, RideCancellationDriverDecision.Rejected, token);
    protected override Task<object?> ReferAsync(RideCancellationRequestModel model, CancellationToken token) => DriverDecideAsync(model, RideCancellationDriverDecision.ReferredToAdmin, token);
    protected override Task<object?> AdminApproveAsync(RideCancellationRequestModel model, CancellationToken token) => AdminDecideAsync(model, true, token);
    protected override Task<object?> AdminRejectAsync(RideCancellationRequestModel model, CancellationToken token) => AdminDecideAsync(model, false, token);

    private async Task<object?> DriverDecideAsync(RideCancellationRequestModel model, RideCancellationDriverDecision decision, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف طلب الإلغاء مطلوب.");
        var driverId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كسائق.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var request = await db.RideCancellationRequests.Include(x => x.Ride).SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        if (request.DriverId != driverId) throw new ServiceException("cancellation_driver_access_denied", "هذا الطلب ليس موجهاً للسائق الحالي.");
        if (request.Status != RideCancellationStatus.DriverReviewPending) return ToResult(request);
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
                db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "أُحيل طلب الإلغاء إلى الإدارة", Body = "رفض السائق طلب الإلغاء ثلاث مرات، لذلك أُحيل الطلب إلى الإدارة للمراجعة.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
            else
            {
                request.Status = RideCancellationStatus.DriverRejected;
                db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "رفض السائق طلب الإلغاء", Body = $"رفض السائق طلب الإلغاء. يمكنك إرسال طلب جديد؛ بعد {3 - rejectionCount} رفضاً إضافياً يُحال الطلب إلى الإدارة.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
        }
        else if (decision == RideCancellationDriverDecision.Accepted)
        {
            var hasPaidPayment = await db.PaymentTransactions.AnyAsync(
                x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid, token);
            if (!hasPaidPayment)
            {
                request.Status = RideCancellationStatus.AdminApproved;
                request.Ride.Status = RideStatus.Cancelled;
                request.Ride.UpdatedAtUtc = DateTime.UtcNow;
                db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "تم إلغاء الرحلة", Body = "وافق السائق على إلغاء الرحلة، وتم إلغاؤها.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
                db.Notifications.Add(new Notification { UserId = request.DriverId!.Value, Type = NotificationType.RideStatus, Title = "تم إلغاء الرحلة", Body = "تم إلغاء الرحلة بناءً على موافقتك.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
            else
            {
                request.Status = RideCancellationStatus.AdminReviewPending;
                await NotifyAdminsAsync(request.Ride, request, "وافق السائق على الإلغاء؛ بانتظار قرار الإدارة المالي.", token);
                db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "طلب الإلغاء قيد مراجعة الإدارة", Body = "أُحيل طلب الإلغاء إلى الإدارة لمراجعة الاسترداد المالي. سيصلك إشعار بالقرار.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
            }
        }
        else
        {
            request.Status = RideCancellationStatus.AdminReviewPending;
            await NotifyAdminsAsync(request.Ride, request, "أحال السائق طلب الإلغاء إلى الإدارة.", token);
            db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "طلب الإلغاء قيد مراجعة الإدارة", Body = "أُحيل طلب الإلغاء إلى الإدارة. سيصلك إشعار بالقرار.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ToResult(request);
    }

    private async Task<object?> AdminDecideAsync(RideCancellationRequestModel model, bool approve, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف طلب الإلغاء مطلوب.");
        var adminId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كمدير.");
        if (!await IsAdminAsync(adminId, token)) throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var request = await db.RideCancellationRequests.Include(x => x.Ride).SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("cancellation_request_not_found", "طلب الإلغاء غير موجود.");
        if (request.Status != RideCancellationStatus.AdminReviewPending) return ToResult(request);
        request.Status = approve ? RideCancellationStatus.AdminApproved : RideCancellationStatus.AdminRejected;
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
            settlement = await SettlePaidCancellationAsync(request, adminId, token);
            request.Ride.Status = RideStatus.Cancelled;
            request.Ride.UpdatedAtUtc = DateTime.UtcNow;
        }
        var customerBody = !approve
            ? "راجعت الإدارة الطلب ولم توافق على الإلغاء المالي."
            : settlement is null
                ? "وافقت الإدارة على طلب الإلغاء وتم إلغاء الرحلة."
                : settlement.CreditedToWallet
                    ? $"وافقت الإدارة على الإلغاء وأُضيف {settlement.RefundAmount:0.##} {settlement.Currency} إلى محفظتك."
                    : "وافقت الإدارة على الإلغاء وسُجل الاسترداد النقدي المباشر من السائق.";
        db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = approve ? "تمت الموافقة على الإلغاء" : "رُفض طلب الإلغاء", Body = customerBody, DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id},\"approved\":{approve.ToString().ToLowerInvariant()},\"settled\":{(settlement is not null).ToString().ToLowerInvariant()}}}" });
        if (request.DriverId is int driverId)
        {
            var driverBody = !approve
                ? "رفضت الإدارة طلب الإلغاء."
                : settlement?.DriverDebtCreated == true
                    ? $"وافقت الإدارة على الإلغاء وأُعيد {settlement.RefundAmount:0.##} {settlement.Currency} إلى محفظة العميل وسُجلت مديونية مستقلة عليك."
                    : settlement is not null && settlement.RefundMethod == CashCancellationRefundMethod.ReturnFromDriver
                        ? "وافقت الإدارة على الإلغاء. سجّل العميل استرداد مبلغ الرحلة منك نقداً مباشرة."
                        : "وافقت الإدارة على إلغاء الرحلة.";
            db.Notifications.Add(new Notification { UserId = driverId, Type = NotificationType.RideStatus, Title = "قرار الإدارة في طلب الإلغاء", Body = driverBody, DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id},\"settled\":{(settlement is not null).ToString().ToLowerInvariant()}}}" });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ToResult(request);
    }

    private async Task<CancellationSettlementResult?> SettlePaidCancellationAsync(
        YemenDrive.Database.Entities.RideCancellationRequest request,
        int actorUserId,
        CancellationToken token)
    {
        var payment = await db.PaymentTransactions.SingleOrDefaultAsync(
            x => x.RideId == request.RideId && x.Status == PaymentStatus.Paid, token);
        if (payment is null) return null;
        var ride = request.Ride;
        if (ride.DriverId is null)
            throw new ServiceException("driver_not_assigned", "لا يمكن تسوية إلغاء رحلة مدفوعة دون سائق مسند.");

        // CancellationFee is an immutable snapshot of the configured policy
        // at offer acceptance.  Keeping it out of the refund preserves the
        // agreed cancellation fee while the source payment stays unchanged.
        var refundAmount = Math.Max(0m, payment.Amount - ride.CancellationFee);
        if (string.Equals(payment.Provider, "Cash", StringComparison.OrdinalIgnoreCase))
            return await SettleCashCancellationAsync(request, payment, refundAmount, actorUserId, token);
        if (string.Equals(payment.Provider, "YemenDriveWallet", StringComparison.OrdinalIgnoreCase))
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
            creditedToWallet, creditedToWallet && refundAmount > 0);
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

        var driverReversal = ride.DriverShare ?? 0m;
        if (driverWallet.Balance < driverReversal)
            throw new ServiceException("driver_settlement_required", "لا يمكن إتمام الاسترداد قبل تسوية صافي مستحق السائق.");
        customerWallet.Balance += refundAmount;
        driverWallet.Balance -= driverReversal;
        db.WalletTransactions.AddRange(
            new WalletTransaction { WalletId = customerWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Refund, Amount = refundAmount, BalanceAfter = customerWallet.Balance, Description = "استرداد إلغاء الرحلة بعد خصم الرسم", ExternalReference = refundReference },
            new WalletTransaction { WalletId = driverWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Debit, Amount = driverReversal, BalanceAfter = driverWallet.Balance, Description = "عكس صافي مستحق السائق بسبب إلغاء الرحلة", ExternalReference = refundReference });
        db.PaymentTransactions.Add(new PaymentTransaction { UserId = ride.CustomerId, RideId = ride.Id, Amount = refundAmount, Currency = originalPayment.Currency, Provider = "YemenDriveWallet", Status = PaymentStatus.Refunded, ProviderReference = refundReference });
        db.DriverSettlements.Add(new DriverSettlement
        {
            DriverId = ride.DriverId!.Value, PeriodStartUtc = DateTime.UtcNow, PeriodEndUtc = DateTime.UtcNow,
            GrossRideAmount = 0, PlatformCommission = 0, Adjustments = -driverReversal,
            NetPayable = -driverReversal, Status = PaymentStatus.Refunded, PaymentReference = refundReference
        });
        await accounting.PostCancellationAsync(ride, originalPayment, refundAmount, true, driverReversal, actorUserId, token);
        return new CancellationSettlementResult(refundAmount, originalPayment.Currency, null, true, false);
    }

    private async Task NotifyAdminsAsync(YemenDrive.Database.Entities.Ride ride, YemenDrive.Database.Entities.RideCancellationRequest request, string body, CancellationToken token)
    {
        var adminIds = await db.Users.AsNoTracking().Where(x => x.Role == UserRole.Admin && x.IsActive).Select(x => x.Id).ToListAsync(token);
        foreach (var adminId in adminIds)
            db.Notifications.Add(new Notification { UserId = adminId, Type = NotificationType.RideStatus, Title = "طلب إلغاء يحتاج مراجعة", Body = body, DataJson = $"{{\"rideId\":{ride.Id},\"cancellationRequestId\":{request.Id}}}" });
    }

    private Task<bool> IsAdminAsync(int userId, CancellationToken token) => db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, token);

    private static object ToResult(YemenDrive.Database.Entities.RideCancellationRequest x) => new
    {
        x.Id, x.RideId, x.CustomerId, x.DriverId, x.RideStatusAtRequest, x.Status, x.DriverDecision,
        x.Reason, x.RequestedRefundMethod, x.RequestedRefundAmount, x.CancellationLatitude, x.CancellationLongitude,
        x.LocationObservedAtUtc, x.DriverDecidedAtUtc, x.DriverNote, x.AdminUserId, x.AdminDecidedAtUtc, x.AdminNote, x.CreatedAtUtc
    };

    private sealed record CancellationSettlementResult(
        decimal RefundAmount,
        string Currency,
        CashCancellationRefundMethod? RefundMethod,
        bool CreditedToWallet,
        bool DriverDebtCreated);
}
