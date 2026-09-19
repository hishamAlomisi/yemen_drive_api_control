using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using CashPaymentRequestEntity = YemenDrive.Database.Entities.CashPaymentRequest;

namespace YemenDrive.Application.DriverPayments;

public sealed class CashPaymentRequest(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<CashPaymentRequestModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["add", "get", "accept", "reject"];

    protected override async Task<object?> AddAsync(CashPaymentRequestModel model, CancellationToken token)
    {
        var customerId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كعميل.");
        if (model.RideId is null) throw new ServiceException("ride_id_required", "معرف الرحلة مطلوب.");
        var key = NormalizeKey(model.IdempotencyKey);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (key is not null)
        {
            var existing = await db.CashPaymentRequests.SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == key, token);
            if (existing is not null) return ToResult(existing);
        }
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.Id == model.RideId, token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != customerId) throw new ServiceException("ride_access_denied", "هذه الرحلة لا تخص العميل الحالي.");
        if (ride.Status != RideStatus.Completed) throw new ServiceException("ride_not_completed", "لا يمكن طلب الدفع النقدي قبل إكمال الرحلة.");
        if (ride.DriverId is null) throw new ServiceException("driver_not_assigned", "السائق غير محدد لهذه الرحلة.");
        if (await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid, token))
            throw new ServiceException("ride_already_paid", "هذه الرحلة مسددة بالفعل.");
        var amount = ride.TotalAmount ?? (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) + ride.ServiceFee;
        if (amount <= 0) throw new ServiceException("fare_not_set", "لا يوجد إجمالي صالح للرحلة.");
        var open = await db.CashPaymentRequests.SingleOrDefaultAsync(x => x.RideId == ride.Id && (x.Status == CashPaymentRequestStatus.Pending || x.Status == CashPaymentRequestStatus.DriverConfirmed), token);
        if (open is not null) return ToResult(open);
        var request = new CashPaymentRequestEntity { RideId = ride.Id, CustomerId = customerId, DriverId = ride.DriverId.Value, Amount = amount, IdempotencyKey = key };
        db.CashPaymentRequests.Add(request);
        db.Notifications.Add(new Notification { UserId = request.DriverId, Type = NotificationType.Payment, Title = "تأكيد دفع نقدي مطلوب", Body = $"هل استلمت مبلغ {amount:0.##} YER نقداً من العميل؟", DataJson = $"{{\"cashPaymentRequestId\":{request.Id},\"rideId\":{ride.Id}}}" });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ToResult(request);
    }

    protected override async Task<object?> GetAsync(CashPaymentRequestModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول.");
        var query = db.CashPaymentRequests.AsNoTracking().AsQueryable();
        if (model.Id is not null) query = query.Where(x => x.Id == model.Id);
        else if (model.RideId is not null) query = query.Where(x => x.RideId == model.RideId);
        else throw new ServiceException("id_required", "معرف الطلب أو الرحلة مطلوب.");
        var request = await query.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(token)
            ?? throw new ServiceException("cash_payment_request_not_found", "لا يوجد طلب دفع نقدي.");
        if (request.CustomerId != userId && request.DriverId != userId) throw new ServiceException("cash_payment_request_access_denied", "لا تملك صلاحية هذا الطلب.");
        return ToResult(request);
    }

    protected override Task<object?> AcceptAsync(CashPaymentRequestModel model, CancellationToken token) => DecideAsync(model, true, token);
    protected override Task<object?> RejectAsync(CashPaymentRequestModel model, CancellationToken token) => DecideAsync(model, false, token);

    private async Task<object?> DecideAsync(CashPaymentRequestModel model, bool accept, CancellationToken token)
    {
        var driverId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كسائق.");
        if (model.Id is null) throw new ServiceException("id_required", "معرف طلب الدفع مطلوب.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var request = await db.CashPaymentRequests.SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("cash_payment_request_not_found", "طلب الدفع النقدي غير موجود.");
        if (request.DriverId != driverId) throw new ServiceException("cash_payment_request_access_denied", "الطلب ليس موجهاً للسائق الحالي.");
        if (request.Status != CashPaymentRequestStatus.Pending) return ToResult(request);
        request.Status = accept ? CashPaymentRequestStatus.DriverConfirmed : CashPaymentRequestStatus.DriverRejected;
        request.DecidedAtUtc = DateTime.UtcNow;
        request.DecisionNote = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim();
        db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.Payment, Title = accept ? "أكد السائق الدفع النقدي" : "لم يؤكد السائق الدفع النقدي", Body = accept ? "أكد السائق الاستلام. سيُسجل التحصيل الآن لإتمام الدفع." : "أفاد السائق بأنه لم يستلم المبلغ النقدي. اختر وسيلة دفع أخرى.", DataJson = $"{{\"cashPaymentRequestId\":{request.Id},\"rideId\":{request.RideId}}}" });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ToResult(request);
    }

    private static string? NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim();
        if (key.Length > 128) throw new ServiceException("invalid_idempotency_key", "مفتاح منع التكرار طويل جداً.");
        return key;
    }

    private static object ToResult(CashPaymentRequestEntity x) => new { x.Id, x.RideId, x.CustomerId, x.DriverId, x.Amount, x.Currency, x.Status, x.DecidedAtUtc, x.DecisionNote, x.CreatedAtUtc };
}
