using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Payments;

public sealed class Payment(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : OperationsService<PaymentModel>(configurationStore)
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
        var ride = await dbContext.Rides.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == model.RideId, cancellationToken)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.Status != RideStatus.Completed)
            throw new ServiceException("ride_not_completed", "لا يمكن تسجيل الدفع قبل اكتمال الرحلة.");
        if (ride.CustomerId != userId)
            throw new ServiceException("invalid_payment_user", "المستخدم لا يطابق عميل الرحلة.");

        var amount = ride.CustomerPrice ?? ride.ServerPrice;
        if (amount is not null && model.Amount != amount.Value)
            throw new ServiceException("invalid_payment_amount", "مبلغ الدفع لا يطابق السعر المتفق عليه.");

        var entity = new PaymentTransaction
        {
            RideId = ride.Id,
            UserId = userId.Value,
            Amount = model.Amount,
            Currency = string.IsNullOrWhiteSpace(model.Currency) ? "YER" : model.Currency.Trim(),
            Provider = string.IsNullOrWhiteSpace(model.Provider) ? "Cash" : model.Provider.Trim(),
            Status = model.Status ?? PaymentStatus.Paid,
            ProviderReference = model.ProviderReference?.Trim(),
            IdempotencyKey = idempotencyKey
        };
        dbContext.PaymentTransactions.Add(entity);
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
