using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Accounting;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Payments;

public sealed class Payment(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser,
    RideAccountingPostingService accounting) : OperationsService<PaymentModel>(configurationStore)
{
    protected override async Task<object?> AddAsync(PaymentModel model, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (model.RideId is null || userId is null || model.Amount <= 0)
            throw new ServiceException("invalid_payment", "الرحلة والمستخدم والمبلغ مطلوبة.");

        var idempotencyKey = NormalizeIdempotencyKey(model.IdempotencyKey);
        if (idempotencyKey is not null)
        {
            var existing = await dbContext.PaymentTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId.Value && x.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (existing is not null) return ToResult(existing, alreadyProcessed: true);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var ride = await dbContext.Rides
            .SingleOrDefaultAsync(x => x.Id == model.RideId, cancellationToken)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.Status != RideStatus.Completed)
            throw new ServiceException("ride_not_completed", "لا يمكن تسجيل الدفع قبل اكتمال الرحلة.");
        if (ride.CustomerId != userId)
            throw new ServiceException("invalid_payment_user", "المستخدم لا يطابق عميل الرحلة.");

        var fare = ride.CustomerPrice ?? ride.ServerPrice
            ?? throw new ServiceException("fare_not_set", "لا يوجد مبلغ متفق عليه لهذه الرحلة.");
        var totalDue = ride.TotalAmount ?? fare + ride.ServiceFee;
        if (model.Amount != totalDue)
            throw new ServiceException("invalid_payment_amount", "مبلغ الدفع لا يطابق إجمالي الرحلة المستحق.");
        var provider = string.IsNullOrWhiteSpace(model.Provider) ? "YemenDriveWallet" : model.Provider.Trim();
        if (!string.Equals(provider, "YemenDriveWallet", StringComparison.OrdinalIgnoreCase))
            throw new ServiceException("payment_provider_not_available", "وسيلة الدفع المختارة غير متاحة بعد.");
        if (model.Status is not null && model.Status != PaymentStatus.Paid)
            throw new ServiceException("invalid_payment_status", "دفعة المحفظة يجب أن تسجل كدفعة معتمدة.");

        var customerWallet = await dbContext.Wallets.SingleOrDefaultAsync(x => x.UserId == ride.CustomerId, cancellationToken)
            ?? throw new ServiceException("wallet_not_found", "محفظة العميل غير موجودة.");
        if (!string.Equals(customerWallet.Currency, string.IsNullOrWhiteSpace(model.Currency) ? customerWallet.Currency : model.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ServiceException("currency_mismatch", "عملة الدفع لا تطابق عملة المحفظة.");
        if (customerWallet.Balance < totalDue)
            throw new ServiceException("insufficient_wallet_balance", "رصيد محفظة العميل لا يكفي لإتمام الدفع.");
        if (ride.DriverId is null)
            throw new ServiceException("driver_not_assigned", "لا يمكن توزيع الدفعة قبل تعيين سائق للرحلة.");
        var driverWallet = await dbContext.Wallets.SingleOrDefaultAsync(x => x.UserId == ride.DriverId, cancellationToken)
            ?? throw new ServiceException("driver_wallet_not_found", "محفظة السائق غير موجودة.");
        if (!string.Equals(customerWallet.Currency, driverWallet.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException("currency_mismatch", "عملة محفظة السائق لا تطابق عملة العميل.");

        customerWallet.Balance -= totalDue;
        dbContext.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = customerWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Debit,
            Amount = totalDue, BalanceAfter = customerWallet.Balance,
            Description = "دفع إجمالي الرحلة من محفظة يمن درايف"
        });
        var driverNetAmount = ride.DriverShare ?? fare - ride.DriverCommissionAmount;
        driverWallet.Balance += driverNetAmount;
        dbContext.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = driverWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Credit,
            Amount = driverNetAmount, BalanceAfter = driverWallet.Balance,
            Description = "إضافة صافي مستحق السائق من الرحلة"
        });

        var entity = new PaymentTransaction
        {
            RideId = ride.Id,
            UserId = userId.Value,
            Amount = totalDue,
            Currency = customerWallet.Currency,
            Provider = "YemenDriveWallet",
            Status = PaymentStatus.Paid,
            ProviderReference = model.ProviderReference?.Trim(),
            IdempotencyKey = idempotencyKey
        };
        dbContext.PaymentTransactions.Add(entity);
        dbContext.DriverSettlements.Add(new DriverSettlement
        {
            DriverId = ride.DriverId.Value,
            PeriodStartUtc = DateTime.UtcNow,
            PeriodEndUtc = DateTime.UtcNow,
            GrossRideAmount = totalDue,
            PlatformCommission = ride.PlatformShare ?? ride.ServiceFee + ride.DriverCommissionAmount,
            Adjustments = 0,
            NetPayable = driverNetAmount,
            Status = PaymentStatus.Paid,
            PaymentReference = $"wallet:ride:{ride.Id}:payment:{idempotencyKey ?? Guid.NewGuid().ToString("N")}" 
        });
        await accounting.PostWalletPaymentAsync(ride, entity, userId.Value, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var existing = await dbContext.PaymentTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId.Value && x.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (existing is not null) return ToResult(existing, alreadyProcessed: true);
            throw;
        }
        return ToResult(entity, alreadyProcessed: false);
    }

    protected override async Task<object?> UpdateAsync(PaymentModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        EnsurePaymentAccess(entity);
        if (entity.Status is PaymentStatus.Paid or PaymentStatus.Refunded or PaymentStatus.Cancelled)
            throw new ServiceException("payment_immutable", "لا يمكن تعديل عملية دفع معتمدة أو مستردة أو ملغاة؛ استخدم عملية مالية عكسية موثقة.");
        if (model.Status is not null) entity.Status = model.Status.Value;
        if (model.ProviderReference is not null) entity.ProviderReference = model.ProviderReference.Trim();
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity, alreadyProcessed: false);
    }

    protected override async Task<object?> GetAsync(PaymentModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken, true);
        EnsurePaymentAccess(entity);
        return ToResult(entity, alreadyProcessed: false);
    }

    private void EnsurePaymentAccess(PaymentTransaction payment)
    {
        if (currentUser.UserId is int userId && payment.UserId != userId)
            throw new ServiceException("payment_access_denied", "لا تملك صلاحية الوصول إلى عملية الدفع.");
    }

    private async Task<PaymentTransaction> FindAsync(int? id, CancellationToken token, bool noTracking = false)
    {
        if (id is null) throw new ServiceException("id_required", "معرف عملية الدفع مطلوب.");
        IQueryable<PaymentTransaction> query = dbContext.PaymentTransactions;
        if (noTracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException("payment_not_found", "عملية الدفع غير موجودة.");
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim();
        if (key.Length > 128)
            throw new ServiceException("invalid_idempotency_key", "مفتاح منع التكرار لا يمكن أن يتجاوز 128 حرفاً.");
        return key;
    }

    private static object ToResult(PaymentTransaction x, bool alreadyProcessed) => new
    {
        x.Id, x.RideId, x.UserId, x.Amount, x.Currency, x.Provider,
        x.Status, x.ProviderReference, x.CreatedAtUtc, x.UpdatedAtUtc, alreadyProcessed
    };
}
