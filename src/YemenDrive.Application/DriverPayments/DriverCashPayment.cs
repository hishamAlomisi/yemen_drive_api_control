using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.DriverPayments;

public sealed class DriverCashPayment(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<DriverCashPaymentModel>(config)
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
        if (ride.Status != RideStatus.InProgress)
            throw new ServiceException("ride_payment_not_ready", "يجب أن تكون الرحلة قيد التنفيذ قبل تسجيل الدفع.");

        var fare = ride.CustomerPrice ?? ride.ServerPrice
            ?? throw new ServiceException("fare_not_set", "لا يوجد مبلغ متفق عليه لهذه الرحلة.");
        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == ride.CustomerId, token)
            ?? throw new ServiceException("wallet_not_found", "محفظة العميل غير موجودة.");
        var difference = model.CashReceived - fare;
        if (difference < 0 && wallet.Balance < Math.Abs(difference))
            throw new ServiceException("insufficient_wallet_balance", "المبلغ المقبوض أقل من الأجرة ورصيد محفظة العميل غير كافٍ.");

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
        }

        var payment = new PaymentTransaction
        {
            UserId = ride.CustomerId, RideId = ride.Id, Amount = fare,
            Currency = string.IsNullOrWhiteSpace(model.Currency) ? "YER" : model.Currency.Trim(),
            Provider = "Cash", Status = PaymentStatus.Paid,
            ProviderReference = cashReference,
            IdempotencyKey = idempotencyKey
        };
        db.PaymentTransactions.Add(payment);
        ride.Status = RideStatus.Completed;
        ride.CompletedAtUtc = DateTime.UtcNow;
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

        return ToResult(ride.Id, payment.Id, fare, model.CashReceived, difference, wallet.Balance, ride.Status, false);
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
        decimal walletAdjustment, decimal walletBalance, RideStatus? rideStatus, bool alreadyProcessed) => new
    {
        rideId, paymentId, fare, cashReceived, walletAdjustment, walletBalance, rideStatus, alreadyProcessed
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
