using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Rides;
using YemenDrive.Application.Payments;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.DriverPayments;

public sealed class DriverCashPayment(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext currentUser, RideAccountingPostingService accounting,
    IRideRealtimePublisher? realtimePublisher = null) : OperationsService<DriverCashPaymentModel>(config)
{
    protected override async Task<object?> AddAsync(DriverCashPaymentModel model, CancellationToken token)
    {
        var driverId = currentUser.RequireUserId();
        if (model.RideId == null || model.CashReceived <= 0 || string.IsNullOrWhiteSpace(model.IdempotencyKey) || model.IdempotencyKey.Length > 128)
            throw new ServiceException("invalid_cash_payment", "حدد الرحلة والنقد المقبوض فعلياً ومرجع تحصيل جديداً لكل قبض.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await RidePaymentGuard.LockAsync(db, model.RideId.Value, token);
        var ride = await db.Rides.SingleAsync(x => x.Id == model.RideId, token);
        if (ride.DriverId != driverId) throw new ServiceException("ride_access_denied", "الرحلة ليست مسندة إليك.");
        var reference = $"cash:ride:{ride.Id}:{model.IdempotencyKey}";
        var existing = await db.PaymentTransactions.SingleOrDefaultAsync(x => x.ProviderReference == reference, token);
        if (existing != null) { if (existing.PhysicalCashReceived != model.CashReceived) throw new ServiceException("cash_reference_conflict", "مرجع التحصيل مستخدم بمبلغ آخر. لم تسجل دفعة جديدة."); await tx.CommitAsync(token); return await ResultAsync(ride, existing, model.CashReceived, true, token); }
        if (ride.Status is not (RideStatus.InProgress or RideStatus.Completed or RideStatus.Cancelled))
            throw new ServiceException("ride_payment_not_ready", "التحصيل النقدي متاح أثناء النقل أو بعد انتهائه.");
        if (ride.PaymentMethodCode != "cash") throw new ServiceException("cash_payment_not_selected", "يجب أن يختار الراكب النقد قبل تسجيل التحصيل.");
        await RidePaymentGuard.EnsureAvailableAsync(db, ride.Id, token);
        await RidePaymentPolicy.EnsureNoPendingDecisionAsync(db, ride.Id, token, collectingConfirmedCash: true);
        var confirmedCash = await db.CashPaymentRequests.SingleOrDefaultAsync(x => x.RideId == ride.Id && x.Status == CashPaymentRequestStatus.DriverConfirmed, token);
        if (confirmedCash != null && model.CashReceived < confirmedCash.Amount) throw new ServiceException("cash_confirmation_amount_conflict", "النقد أقل من المبلغ الذي أكدت استلامه سابقاً. تحتاج المطالبة إلى مراجعة قبل تسجيل مبلغ مختلف.");
        await new RideWaitingCoordinator(db).EnsureSettledAsync(ride, token);
        var money = await RideFinancialState.ReadAsync(db, ride, token);
        if (money.AmountUnderReview) throw new ServiceException("cancellation_amount_under_review", "تحتاج أجرة الإلغاء إلى مراجعة الجزء المنفذ والمسؤولية قبل التحصيل.");
        if (money.OutstandingAmount <= 0) throw new ServiceException("ride_already_paid", "لا يوجد مبلغ رحلة متبقٍ للتحصيل. لا تسجل النقد مرة أخرى.");
        var applied = Math.Min(model.CashReceived, money.OutstandingAmount);
        var excess = model.CashReceived - applied;
        var payment = new PaymentTransaction { RideId = ride.Id, UserId = ride.CustomerId, Amount = applied,
            Currency = "YER", Provider = "Cash", PhysicalCashReceived = model.CashReceived, Status = PaymentStatus.Paid,
            ProviderReference = reference, IdempotencyKey = model.IdempotencyKey.Trim() };
        var allocation = await RideFinancialState.AllocateReceiptAsync(db, ride, payment, token);
        db.PaymentTransactions.Add(payment);
        await accounting.PostCashCollectionAsync(allocation, payment, applied, 0m, driverId, token);
        var driverDebt = applied - (payment.DriverNetAmount ?? 0m);
        if (driverDebt > 0) db.DriverSettlements.Add(new() { DriverId = driverId, PeriodStartUtc = DateTime.UtcNow,
            PeriodEndUtc = DateTime.UtcNow, GrossRideAmount = applied, PlatformCommission = driverDebt,
            NetPayable = -driverDebt, Status = PaymentStatus.Pending, PaymentReference = reference });
        RideFinancialCase? change = null;
        if (excess > 0)
        {
            change = new() { RideId = ride.Id, CustomerId = ride.CustomerId, DriverId = driverId, Kind = "CashChange",
                Status = "AwaitingCustomer", Amount = excess, SourceReference = $"cash-change:{ride.Id}:{model.IdempotencyKey}",
                Note = "الباقي النقدي حق للعميل. الأصل إعادته نقداً؛ إضافته للمحفظة تتطلب اختياره الصريح." };
            db.RideFinancialCases.Add(change);
            await RideFinancialCaseService.CashChangePostingAsync(db, change, false, driverId, token, hold: true);
        }
        await db.SaveChangesAsync(token);
        await RideRouteSettlement.CompleteApprovedAsync(db, ride, token);
        if (confirmedCash != null) { confirmedCash.Status = CashPaymentRequestStatus.Collected; confirmedCash.DecidedAtUtc = DateTime.UtcNow; }
            await db.SaveChangesAsync(token);
            await RideFinancialState.SynchronizeAsync(db, ride, driverId, token);
        db.Notifications.Add(new() { UserId = ride.CustomerId, Type = NotificationType.Payment,
            Title = excess > 0 ? "اختر طريقة استلام الباقي النقدي" : "تم تسجيل النقد المقبوض",
            Body = excess > 0 ? $"الباقي {excess:0.##} ر.ي: استلمه نقداً، أو اختر إضافته لمحفظتك." : $"المقبوض {applied:0.##} ر.ي. المتبقي {Math.Max(0, money.OutstandingAmount - applied):0.##} ر.ي.",
            DataJson = $"{{\"action\":\"open_ride_payment\",\"rideId\":{ride.Id},\"financialCaseId\":{change?.Id ?? 0}}}" });
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        if (realtimePublisher != null) await realtimePublisher.PublishPaymentUpdatedAsync([ride.CustomerId, driverId], new(ride.Id, "cashPaymentRecorded", DateTime.UtcNow, PaymentId: payment.Id), token);
        return await ResultAsync(ride, payment, model.CashReceived, false, token);
    }
    private async Task<object> ResultAsync(YemenDrive.Database.Entities.Ride ride, PaymentTransaction payment, decimal cash, bool repeated, CancellationToken ct)
    {
        var money = await RideFinancialState.ReadAsync(db, ride, ct);
        return new { rideId = ride.Id, paymentId = payment.Id, totalDue = money.AmountDue, cashReceived = cash,
            collectedAmount = payment.Amount, money.OutstandingAmount, money.PaymentCompleted,
            walletAdjustment = 0m, walletBalance = await db.Wallets.Where(x => x.UserId == ride.CustomerId).Select(x => x.Balance).FirstAsync(ct),
            rideStatus = ride.Status, alreadyProcessed = repeated,
            message = money.PaymentCompleted ? "سُجل التحصيل بالكامل." : "سُجل النقد المقبوض فقط؛ يبقى الفرق مستحقاً ويمكن إنهاء النقل عند اكتماله فعلياً." };
    }
}
