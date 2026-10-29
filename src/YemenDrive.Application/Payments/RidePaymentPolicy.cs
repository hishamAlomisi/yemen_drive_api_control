using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Payments;

public static class RidePaymentPolicy
{
    public static bool IsBeforeStart(RideStatus status) =>
        status is RideStatus.DriverAssigned or RideStatus.DriverEnRoute or RideStatus.DriverArrived;

    public static bool RequiresBeforeStart(Ride ride) =>
        IsBeforeStart(ride.Status) && ride.PaymentRequiredBeforeStart;

    public static async Task<string> NormalizeChoiceAsync(YemenDriveDbContext db, string? value, CancellationToken token)
    {
        var code = string.IsNullOrWhiteSpace(value) ? "cash" : value.Trim();
        if (code.Equals("cash", StringComparison.OrdinalIgnoreCase)) return "cash";
        if (code.Equals("wallet", StringComparison.OrdinalIgnoreCase)) return "wallet";
        if (code.Length > 80)
            throw new ServiceException("invalid_payment_method", "وسيلة الدفع المختارة غير صالحة.");
        var method = await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, token);
        if (method is null || !method.IsActive || !method.IsAvailableForRidePayment ||
            method.Kind != PaymentMethodKind.ExternalWallet || method.ProviderCode is not ("Floosak" or "Jaib"))
            throw new ServiceException("payment_method_unavailable", "وسيلة الدفع المختارة غير متاحة لدفع الرحلة.");
        return method.Code;
    }

    public static void OnOfferAccepted(Ride ride) =>
        ride.PaymentRequiredBeforeStart = !ride.PaymentMethodCode.Equals("cash", StringComparison.OrdinalIgnoreCase);

    public static void EnsureReady(Ride ride, string? payingMethodCode = null)
    {
        if (IsBeforeStart(ride.Status))
        {
            if (!ride.PaymentRequiredBeforeStart || ride.PaymentMethodCode == "cash")
                throw new ServiceException("ride_payment_not_ready", "لم تصبح الرحلة جاهزة للدفع بهذه الوسيلة بعد.");
            if (payingMethodCode is not null && !ride.PaymentMethodCode.Equals(payingMethodCode, StringComparison.OrdinalIgnoreCase))
                throw new ServiceException("ride_payment_method_mismatch", "استخدم وسيلة الدفع المختارة عند الحجز أو حوّل الدفع إلى النقد.");
            return;
        }
        if (ride.Status != RideStatus.Completed && !(ride.Status == RideStatus.InProgress && ride.CustomerPaymentEnabled))
            throw new ServiceException("ride_payment_not_ready", "لم تصبح الرحلة جاهزة للدفع بعد.");
    }

    public static async Task EnsureCanStartAsync(YemenDriveDbContext db, Ride ride, CancellationToken token)
    {
        if (ride.PaymentMethodCode != "cash" &&
            !await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid, token))
            throw new ServiceException("payment_required_before_start", "يجب على الراكب إتمام الدفع المختار أو التحويل إلى النقد قبل بدء الرحلة.");
        if (await db.RideCancellationRequests.AnyAsync(x => x.RideId == ride.Id &&
            (x.Status == RideCancellationStatus.DriverReviewPending || x.Status == RideCancellationStatus.AdminReviewPending ||
             x.Status == RideCancellationStatus.CustomerReviewPending), token))
            throw new ServiceException("ride_cancellation_pending", "يجب حسم طلب الإلغاء قبل بدء الرحلة.");
    }

    public static async Task EnsureNoPendingDecisionAsync(YemenDriveDbContext db, int rideId, CancellationToken token)
    {
        if (await db.RideCancellationRequests.AnyAsync(x => x.RideId == rideId &&
            (x.Status == RideCancellationStatus.DriverReviewPending || x.Status == RideCancellationStatus.AdminReviewPending ||
             x.Status == RideCancellationStatus.CustomerReviewPending), token))
            throw new ServiceException("ride_cancellation_pending", "يجب حسم طلب الإلغاء قبل الدفع أو تغيير وسيلته.");
        if (await db.CashCollectionApprovals.AnyAsync(x => x.RideId == rideId &&
                (x.Status == CashCollectionApprovalStatus.Pending || x.Status == CashCollectionApprovalStatus.Approved), token) ||
            await db.CashPaymentRequests.AnyAsync(x => x.RideId == rideId &&
                (x.Status == CashPaymentRequestStatus.Pending || x.Status == CashPaymentRequestStatus.DriverConfirmed), token))
            throw new ServiceException("cash_payment_pending", "يوجد إجراء دفع نقدي جارٍ لهذه الرحلة. أكمله أو ارفضه أولاً.");
    }
}
