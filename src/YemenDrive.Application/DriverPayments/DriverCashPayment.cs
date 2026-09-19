using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Accounting;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using CashCollectionApprovalEntity = YemenDrive.Database.Entities.CashCollectionApproval;
using CashPaymentRequestEntity = YemenDrive.Database.Entities.CashPaymentRequest;

namespace YemenDrive.Application.DriverPayments;

public sealed class DriverCashPayment(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser,
    RideAccountingPostingService accounting) : OperationsService<DriverCashPaymentModel>(config)
{
    protected override async Task<object?> AddAsync(DriverCashPaymentModel model, CancellationToken token)
    {
        var driverId = currentUser.UserId ?? throw new ServiceException(
            "authentication_required", "يجب تسجيل الدخول كسائق.");
        if (model.RideId is null || model.CashReceived < 0)
            throw new ServiceException("invalid_cash_payment", "الرحلة والمبلغ المقبوض مطلوبان.");

        var idempotencyKey = NormalizeIdempotencyKey(model.IdempotencyKey);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.Id == model.RideId.Value, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");

        var cashReference = $"cash:ride:{ride.Id}:driver:{driverId}";
        if (idempotencyKey is not null)
        {
            var existing = await db.PaymentTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == ride.CustomerId && x.IdempotencyKey == idempotencyKey, token);
            if (existing is not null)
                return await ToExistingResultAsync(existing, token);
        }

        var existingCashPayment = await db.PaymentTransactions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProviderReference == cashReference, token);
        if (existingCashPayment is not null)
            return await ToExistingResultAsync(existingCashPayment, token);

        if (ride.DriverId != driverId)
            throw new ServiceException("ride_access_denied", "هذه الرحلة ليست مسندة إلى السائق الحالي.");
        CashPaymentRequestEntity? confirmedCustomerCashRequest = null;
        if (ride.Status != RideStatus.InProgress)
        {
            if (ride.Status != RideStatus.Completed)
                throw new ServiceException("ride_payment_not_ready", "لا يمكن تسجيل التحصيل النقدي في حالة الرحلة الحالية.");
            confirmedCustomerCashRequest = await db.CashPaymentRequests.SingleOrDefaultAsync(x =>
                x.RideId == ride.Id && x.DriverId == driverId &&
                x.Status == CashPaymentRequestStatus.DriverConfirmed, token);
            if (confirmedCustomerCashRequest is null)
                throw new ServiceException("cash_confirmation_required", "يلزم تأكيد طلب العميل للدفع النقدي أولاً.");
        }

        var fare = ride.CustomerPrice ?? ride.ServerPrice
            ?? throw new ServiceException("fare_not_set", "لا يوجد مبلغ متفق عليه لهذه الرحلة.");
        var totalDue = ride.TotalAmount ?? fare + ride.ServiceFee;
        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == ride.CustomerId, token)
            ?? throw new ServiceException("wallet_not_found", "محفظة العميل غير موجودة.");
        var currency = string.IsNullOrWhiteSpace(model.Currency) ? wallet.Currency : model.Currency.Trim();
        if (!string.Equals(currency, wallet.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException("currency_mismatch", "عملة التحصيل لا تطابق عملة محفظة العميل.");
        var difference = model.CashReceived - totalDue;
        CashCollectionApprovalEntity? approvedShortfall = null;
        if (difference < 0)
        {
            var shortfall = Math.Abs(difference);
            approvedShortfall = await db.CashCollectionApprovals.SingleOrDefaultAsync(x =>
                x.RideId == ride.Id && x.DriverId == driverId &&
                x.Status == CashCollectionApprovalStatus.Approved &&
                x.CashReceived == model.CashReceived && x.WalletDebitAmount == shortfall, token);
            if (approvedShortfall is null)
            {
                var pending = await db.CashCollectionApprovals.SingleOrDefaultAsync(x =>
                    x.RideId == ride.Id && x.Status == CashCollectionApprovalStatus.Pending, token);
                if (pending is not null)
                {
                    await transaction.CommitAsync(token);
                    return new { rideId = ride.Id, requiresCustomerApproval = true, approvalId = pending.Id, walletDebitAmount = pending.WalletDebitAmount, message = "بانتظار موافقة العميل على خصم الفرق من محفظته." };
                }
                if (wallet.Balance < shortfall)
                {
                    db.Notifications.Add(new Notification { UserId = ride.CustomerId, Type = NotificationType.Payment, Title = "تعذر تغطية فرق الرحلة", Body = "رصيد محفظتك لا يكفي لتغطية الفرق المطلوب من الدفع النقدي." });
                    await db.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    return new { rideId = ride.Id, rejected = true, reason = "insufficient_wallet_balance", message = "رصيد محفظة العميل لا يكفي لتغطية الفرق؛ لم تسجل العملية." };
                }
                var approval = new CashCollectionApprovalEntity
                {
                    RideId = ride.Id, DriverId = driverId, CustomerId = ride.CustomerId,
                    CashReceived = model.CashReceived, WalletDebitAmount = shortfall,
                    Currency = currency, IdempotencyKey = idempotencyKey
                };
                db.CashCollectionApprovals.Add(approval);
                await db.SaveChangesAsync(token);
                db.Notifications.Add(new Notification { UserId = ride.CustomerId, Type = NotificationType.Payment, Title = "موافقة مطلوبة لتغطية فرق الرحلة", Body = $"استلم السائق مبلغاً أقل من إجمالي الرحلة. وافق على خصم {shortfall:0.##} {currency} من محفظتك لإتمام التحصيل.", DataJson = $"{{\"cashCollectionApprovalId\":{approval.Id},\"rideId\":{ride.Id}}}" });
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return new { rideId = ride.Id, requiresCustomerApproval = true, approvalId = approval.Id, walletDebitAmount = shortfall, message = "أُرسل طلب موافقة للعميل؛ لن تكتمل الرحلة قبل قراره." };
            }
        }

        if (difference > 0)
        {
            wallet.Balance += difference;
            db.WalletTransactions.Add(new WalletTransaction
            {
                WalletId = wallet.Id, RideId = ride.Id,
                Type = WalletTransactionType.Credit, Amount = difference,
                BalanceAfter = wallet.Balance,
                Description = "إرجاع المبلغ الزائد من الدفع النقدي"
            });
            db.Notifications.Add(new Notification
            {
                UserId = ride.CustomerId,
                Type = NotificationType.Payment,
                Title = "أُضيف المبلغ الزائد إلى محفظتك",
                Body = $"أعاد السائق {difference:0.##} {currency} إلى محفظتك من الرحلة #{ride.Id}.",
                DataJson = $"{{\"rideId\":{ride.Id},\"walletAdjustment\":{difference.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"
            });
        }
        else if (difference < 0)
        {
            var debit = Math.Abs(difference);
            wallet.Balance -= debit;
            db.WalletTransactions.Add(new WalletTransaction
            {
                WalletId = wallet.Id, RideId = ride.Id,
                Type = WalletTransactionType.Debit, Amount = debit,
                BalanceAfter = wallet.Balance,
                Description = "خصم الفرق المتبقي من أجرة الرحلة"
            });
            db.Notifications.Add(new Notification
            {
                UserId = ride.CustomerId,
                Type = NotificationType.Payment,
                Title = "خُصم الفرق من محفظتك",
                Body = $"خُصم {debit:0.##} {currency} لإكمال دفع الرحلة #{ride.Id}.",
                DataJson = $"{{\"rideId\":{ride.Id},\"walletAdjustment\":-{debit.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"
            });
        }

        var platformReceivable = ride.PlatformShare ?? ride.ServiceFee + ride.DriverCommissionAmount;
        var payment = new PaymentTransaction
        {
            UserId = ride.CustomerId, RideId = ride.Id, Amount = totalDue,
            Currency = currency,
            Provider = "Cash", Status = PaymentStatus.Paid,
            ProviderReference = cashReference,
            IdempotencyKey = idempotencyKey
        };
        db.PaymentTransactions.Add(payment);
        // The driver physically holds every cash amount received. Besides the
        // platform share, any excess returned to the customer's wallet is a
        // separate receivable from that same driver; it must therefore appear
        // in the settlement balance as well as in the ledger journal.
        var customerWalletExcess = Math.Max(0m, difference);
        var driverSettlementReceivable = platformReceivable + customerWalletExcess;
        if (driverSettlementReceivable > 0)
        {
            // With cash, the driver physically holds the whole amount. The
            // platform share and any excess credited to the customer wallet
            // remain documented debts; no driver wallet is debited.
            db.DriverSettlements.Add(new DriverSettlement
            {
                DriverId = driverId,
                PeriodStartUtc = DateTime.UtcNow,
                PeriodEndUtc = DateTime.UtcNow,
                GrossRideAmount = totalDue,
                PlatformCommission = platformReceivable,
                Adjustments = customerWalletExcess,
                NetPayable = -driverSettlementReceivable,
                Status = PaymentStatus.Pending,
                PaymentReference = cashReference
            });
        }
        await accounting.PostCashCollectionAsync(ride, payment, model.CashReceived, difference, driverId, token);
        if (confirmedCustomerCashRequest is not null)
        {
            confirmedCustomerCashRequest.Status = CashPaymentRequestStatus.Collected;
            confirmedCustomerCashRequest.DecidedAtUtc = DateTime.UtcNow;
        }
        // Cash collection is financial, not operational: the driver still
        // chooses when the trip reaches its real completion state.
        ride.UpdatedAtUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            var concurrentPayment = await db.PaymentTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProviderReference == cashReference, token);
            if (concurrentPayment is not null)
                return await ToExistingResultAsync(concurrentPayment, token);
            throw;
        }

        return ToResult(ride.Id, payment.Id, totalDue, model.CashReceived, difference, wallet.Balance, ride.Status, false,
            ride.ServiceFee, ride.DriverCommissionAmount, platformReceivable);
    }

    private async Task<object> ToExistingResultAsync(PaymentTransaction payment, CancellationToken token)
    {
        var walletBalance = await db.Wallets.AsNoTracking()
            .Where(x => x.UserId == payment.UserId)
            .Select(x => x.Balance)
            .SingleOrDefaultAsync(token);
        var ride = payment.RideId is int rideId
            ? await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rideId, token)
            : null;
        return ToResult(payment.RideId, payment.Id, payment.Amount, null, 0, walletBalance, ride?.Status, true);
    }

    private static object ToResult(int? rideId, int paymentId, decimal fare, decimal? cashReceived,
        decimal walletAdjustment, decimal walletBalance, RideStatus? rideStatus, bool alreadyProcessed,
        decimal serviceFee = 0, decimal driverCommission = 0, decimal platformReceivable = 0) => new
    {
        rideId, paymentId, totalDue = fare, cashReceived, walletAdjustment, walletBalance, rideStatus,
        serviceFee, driverCommission, platformReceivable, alreadyProcessed
    };

    private static string? NormalizeIdempotencyKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim();
        if (key.Length > 128)
            throw new ServiceException("invalid_idempotency_key", "مفتاح منع التكرار لا يمكن أن يتجاوز 128 حرفاً.");
        return key;
    }
}
