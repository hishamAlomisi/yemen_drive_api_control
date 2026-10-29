using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Payments;

/// <summary>Posts only durable, provider-confirmed withdrawals. It never calls the provider.</summary>
public sealed class ExternalWalletPaymentSettlement(
    YemenDriveDbContext db,
    RideAccountingPostingService accounting,
    IRideRealtimePublisher? realtimePublisher = null,
    ILogger<ExternalWalletPaymentSettlement>? logger = null)
{
    public async Task ValidateAsync(YemenDrive.Database.Entities.Ride ride, int paymentMethodId, CancellationToken token)
    {
        if (ride.DriverId is null || !await db.Wallets.AnyAsync(x => x.UserId == ride.DriverId && x.Currency == "YER", token))
            throw new ServiceException("driver_wallet_not_found", "محفظة السائق غير مجهزة بالريال اليمني.");
        var fare = (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) + ride.WaitingFee;
        var totalDue = ride.TotalAmount ?? fare + ride.ServiceFee;
        var driverNet = ride.DriverShare ?? fare - ride.DriverCommissionAmount;
        if (totalDue <= 0 || driverNet < 0 || driverNet > totalDue || ride.ServiceFee < 0 || ride.DriverCommissionAmount < 0)
            throw new ServiceException("invalid_ride_amount", "المبالغ المثبتة للرحلة تحتاج إلى مراجعة.");
        await accounting.ValidateExternalWalletAccountsAsync(ride, paymentMethodId, token);
    }

    public async Task<PaymentTransaction> SettleAsync(PaymentWalletOperation operation, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await RidePaymentGuard.LockAsync(db, operation.RideId, token);
        await db.Entry(operation).ReloadAsync(token);
        if (operation.Status == "Succeeded" && operation.PaymentTransactionId is int existingId)
        {
            var existing = await db.PaymentTransactions.SingleAsync(x => x.Id == existingId, token);
            await transaction.CommitAsync(token);
            return existing;
        }
        if (operation.Status != "ProviderSucceeded" || operation.ProviderSucceededAtUtc is null ||
            operation.ProviderCode is not ("Floosak" or "Jaib"))
            throw new ServiceException("provider_payment_not_confirmed", "لم يؤكد المزود إتمام الخصم.");
        var ride = await db.Rides.SingleAsync(x => x.Id == operation.RideId, token);
        await db.Entry(ride).ReloadAsync(token);
        if (ride.CustomerId != operation.CustomerId || ride.DriverId is null ||
            ride.Status is not (RideStatus.DriverAssigned or RideStatus.DriverEnRoute or RideStatus.DriverArrived or RideStatus.InProgress or RideStatus.Completed))
            throw new ServiceException("wallet_settlement_requires_review", "تحتاج الدفعة المؤكدة إلى مراجعة بيانات الرحلة.");
        var totalDue = ride.TotalAmount ?? (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) + ride.WaitingFee + ride.ServiceFee;
        if (operation.Amount <= 0 || operation.Amount != totalDue || operation.Currency != "YER")
            throw new ServiceException("wallet_settlement_amount_mismatch", "تحتاج الدفعة المؤكدة إلى مراجعة المبلغ أو العملة.");
        if (await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid, token))
            throw new ServiceException("ride_already_paid", "توجد دفعة أخرى معتمدة لهذه الرحلة؛ يلزم مراجعتها.");
        var method = await db.PaymentMethods.SingleAsync(x => x.Id == operation.PaymentMethodId, token);
        if (!string.Equals(method.ProviderCode, operation.ProviderCode, StringComparison.Ordinal))
            throw new ServiceException("wallet_settlement_provider_mismatch", "مزود الدفعة لا يطابق حساب طريقة الدفع؛ يلزم مراجعتها.");
        var driverWallet = await db.Wallets.FromSqlInterpolated(
                $"SELECT * FROM [Wallets] WITH (UPDLOCK,HOLDLOCK) WHERE [UserId] = {ride.DriverId.Value}")
            .SingleOrDefaultAsync(token)
            ?? throw new ServiceException("driver_wallet_not_found", "محفظة السائق غير موجودة.");
        await db.Entry(driverWallet).ReloadAsync(token);
        if (driverWallet.Currency != "YER")
            throw new ServiceException("currency_mismatch", "عملة محفظة السائق لا تطابق عملة الدفعة.");
        var fare = (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) + ride.WaitingFee;
        var driverNet = ride.DriverShare ?? fare - ride.DriverCommissionAmount;
        if (driverNet < 0 || driverNet > totalDue)
            throw new ServiceException("invalid_driver_share", "صافي مستحق السائق يحتاج إلى مراجعة.");
        var now = DateTime.UtcNow;
        var payment = new PaymentTransaction
        {
            RideId = ride.Id, UserId = ride.CustomerId, Amount = totalDue, Currency = "YER",
            // Catalog codes are editable display identifiers and must never select a built-in refund path.
            Provider = operation.ProviderCode, Status = PaymentStatus.Paid,
            // References from different providers can have the same value. Keep the raw reference
            // on the operation/receipt, and namespace its digest for the global payment unique index.
            ProviderReference = $"{operation.ProviderCode}:{operation.PaymentMethodId}:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(operation.ProviderReference ?? operation.RequestId))),
            IdempotencyKey = $"external-wallet:{operation.Id}"
        };
        db.PaymentTransactions.Add(payment);
        driverWallet.Balance += driverNet;
        if (driverNet > 0) db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = driverWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Credit,
            Amount = driverNet, BalanceAfter = driverWallet.Balance,
            Description = $"صافي مستحق الرحلة المدفوعة عبر {method.NameAr}"
        });
        db.DriverSettlements.Add(new DriverSettlement
        {
            DriverId = ride.DriverId.Value, PeriodStartUtc = now, PeriodEndUtc = now,
            GrossRideAmount = totalDue, PlatformCommission = ride.PlatformShare ?? ride.ServiceFee + ride.DriverCommissionAmount,
            NetPayable = driverNet, Status = PaymentStatus.Paid,
            PaymentReference = $"external-wallet:ride:{ride.Id}:operation:{operation.Id}"
        });
        await accounting.PostExternalWalletPaymentAsync(ride, payment, method.Id, ride.CustomerId, token);
        await db.SaveChangesAsync(token);
        operation.Status = "Succeeded";
        operation.NextAction = "None";
        operation.ErrorCode = null;
        operation.PaymentTransactionId = payment.Id;
        operation.PaidAtUtc = operation.ProviderSucceededAtUtc;
        operation.UpdatedAtUtc = now;
        ride.PaymentMethodCode = method.Code;
        ride.PaymentRequiredBeforeStart = false;
        ride.CustomerPaymentEnabled = false;
        ride.UpdatedAtUtc = now;
        // An already confirmed electronic payment closes any obsolete cash prompts.
        var cashRequests = await db.CashPaymentRequests.Where(x => x.RideId == ride.Id &&
            (x.Status == CashPaymentRequestStatus.Pending || x.Status == CashPaymentRequestStatus.DriverConfirmed)).ToListAsync(token);
        foreach (var request in cashRequests)
        {
            request.Status = CashPaymentRequestStatus.DriverRejected;
            request.DecidedAtUtc = now;
            request.DecisionNote = "سُددت الرحلة عبر المحفظة الخارجية.";
        }
        var approvals = await db.CashCollectionApprovals.Where(x => x.RideId == ride.Id &&
            (x.Status == CashCollectionApprovalStatus.Pending || x.Status == CashCollectionApprovalStatus.Approved)).ToListAsync(token);
        foreach (var approval in approvals)
        {
            approval.Status = CashCollectionApprovalStatus.Rejected;
            approval.DecidedAtUtc = now;
            approval.DecisionNote = "سُددت الرحلة عبر المحفظة الخارجية.";
        }
        db.Notifications.AddRange(
            new Notification { UserId = ride.CustomerId, Type = NotificationType.Payment, Title = "تم دفع قيمة الرحلة",
                Body = $"تم دفع {totalDue:0.##} ريال يمني عبر {method.NameAr}.",
                DataJson = $"{{\"action\":\"open_ride_payment\",\"rideId\":{ride.Id},\"paymentId\":{payment.Id}}}" },
            new Notification { UserId = ride.DriverId.Value, Type = NotificationType.Payment, Title = "سُددت الرحلة إلكترونياً",
                Body = "تم الدفع وأُضيف صافي مستحقك إلى محفظتك. لا يلزم تحصيل مبلغ نقدي من الراكب.",
                DataJson = $"{{\"action\":\"open_active_ride\",\"rideId\":{ride.Id},\"paymentId\":{payment.Id}}}" });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        if (realtimePublisher is not null)
        {
            try
            {
                await realtimePublisher.PublishPaymentUpdatedAsync([ride.CustomerId, ride.DriverId.Value],
                    new PaymentRealtimeEvent(ride.Id, "externalWalletPaymentRecorded", now, PaymentId: payment.Id), token);
            }
            catch (Exception)
            {
                logger?.LogWarning("Payment {PaymentId} committed; realtime delivery will be recovered by refresh", payment.Id);
            }
        }
        return payment;
    }
}
