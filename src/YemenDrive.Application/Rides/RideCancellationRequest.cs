using System.Data;
using Microsoft.EntityFrameworkCore;
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
    ICurrentUserContext currentUser) : OperationsService<RideCancellationRequestModel>(config)
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
        var rejections = await db.RideCancellationRequests.CountAsync(
            x => x.RideId == ride.Id && x.DriverDecision == RideCancellationDriverDecision.Rejected, token);
        var reviewByAdmin = isInProgress || ride.DriverId is null || rejections >= 2;
        var request = new YemenDrive.Database.Entities.RideCancellationRequest
        {
            RideId = ride.Id,
            CustomerId = customerId,
            DriverId = ride.DriverId,
            RideStatusAtRequest = ride.Status,
            Status = reviewByAdmin ? RideCancellationStatus.AdminReviewPending : RideCancellationStatus.DriverReviewPending,
            DriverDecision = RideCancellationDriverDecision.None,
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

        ride.Status = RideStatus.CancellationPending;
        ride.UpdatedAtUtc = DateTime.UtcNow;
        db.RideCancellationRequests.Add(request);
        await db.SaveChangesAsync(token);
        if (reviewByAdmin)
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
            request.Status = RideCancellationStatus.DriverRejected;
            db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = "رفض السائق طلب الإلغاء", Body = "رفض السائق طلب الإلغاء. يمكنك إرسال طلب جديد؛ وبعد الرفض الثالث يُحال الطلب تلقائياً إلى الإدارة.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        }
        else
        {
            request.Status = RideCancellationStatus.AdminReviewPending;
            await NotifyAdminsAsync(request.Ride, request, decision == RideCancellationDriverDecision.Accepted ? "وافق السائق على الإلغاء؛ بانتظار قرار الإدارة المالي." : "رفض السائق وأحال طلب الإلغاء إلى الإدارة.", token);
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
        if (approve)
        {
            request.Ride.Status = RideStatus.Cancelled;
            request.Ride.UpdatedAtUtc = DateTime.UtcNow;
        }
        db.Notifications.Add(new Notification { UserId = request.CustomerId, Type = NotificationType.RideStatus, Title = approve ? "تمت الموافقة على الإلغاء" : "رُفض طلب الإلغاء", Body = approve ? "وافقت الإدارة على طلب الإلغاء. تُعالج أي تسوية مالية منفصلة وفق وسيلة الدفع." : "راجعت الإدارة الطلب ولم توافق على الإلغاء المالي.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id},\"approved\":{approve.ToString().ToLowerInvariant()}}}" });
        if (request.DriverId is int driverId)
            db.Notifications.Add(new Notification { UserId = driverId, Type = NotificationType.RideStatus, Title = "قرار الإدارة في طلب الإلغاء", Body = approve ? "وافقت الإدارة على إلغاء الرحلة." : "رفضت الإدارة طلب الإلغاء.", DataJson = $"{{\"rideId\":{request.RideId},\"cancellationRequestId\":{request.Id}}}" });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ToResult(request);
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
}
