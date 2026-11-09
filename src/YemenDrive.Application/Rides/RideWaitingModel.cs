using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public sealed record RideWaitingModel(int? RideId = null, int? SessionId = null,
    Guid? RequestKey = null, Guid? ResumeRequestKey = null);

public sealed class RideWaiting(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user, RideWaitingCoordinator waiting, TimeProvider? timeProvider = null) : ModelService<RideWaitingModel>(config)
{
    private DateTime UtcNow => (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    public override IReadOnlyCollection<string> Operations { get; } =
        ["get", "start", "resume", "request_resume", "accept_resume", "reject_resume", "add", "update", "accept", "reject", "cancel"];

    protected override async Task<object?> ExecuteOperationAsync(string operation, RideWaitingModel m, CancellationToken ct)
    {
        operation = operation.ToLowerInvariant() switch
        {
            "add" => "start", "update" => "resume", "accept" => "accept_resume", "reject" => "reject_resume",
            var value => value
        };
        if (!Operations.Contains(operation)) throw new ServiceException("operation_not_supported", "عملية الانتظار غير صحيحة.");
        var id = user.UserId ?? throw new ServiceException("authentication_required", "سجّل الدخول لإدارة الانتظار.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (m.RideId is int rideId) await RidePaymentGuard.LockAsync(db, rideId, ct);
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.Id == m.RideId, ct)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        var participant = id == ride.CustomerId || id == ride.DriverId;
        var admin = !participant && operation == "get" && await db.Users.AnyAsync(x => x.Id == id && x.IsActive && x.Role == UserRole.Admin, ct);
        if (!participant && !admin) throw new ServiceException("ride_access_denied", "هذه الرحلة لا تخصك.");
        if (operation is "start" or "resume" or "accept_resume" or "reject_resume" && ride.DriverId != id)
            throw new ServiceException("driver_action_required", "بدء التوقف واستئناف الرحلة متاحان للسائق المسند فقط.");
        if (operation == "request_resume" && ride.CustomerId != id)
            throw new ServiceException("rider_action_required", "طلب استئناف الرحلة متاح للراكب فقط.");
        if (operation == "cancel")
            throw new ServiceException("waiting_stop_approval_removed", "التوقف يبدأ مباشرةً بأمر السائق؛ لا يوجد طلب توقف لإلغائه أو الموافقة عليه.");

        var now = UtcNow;
        var session = await waiting.ReconcileAsync(ride, now, ct);
        var changed = db.ChangeTracker.HasChanges();
        // Release the filtered index after reconciliation before another stop.
        if (changed) await db.SaveChangesAsync(ct);
        if (operation == "start")
        {
            RequireRequestKey(m.RequestKey);
            var existing = await db.RideWaitingSessions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.RideId == ride.Id && x.RequestKey == m.RequestKey, ct);
            if (existing is not null && existing.RequestedByUserId != id)
                throw new ServiceException("waiting_request_key_mismatch", "معرف التوقف مستخدم لطلب آخر.");
            if (existing is null)
            {
                await EnsureCanWaitAsync(ride, ct);
                if (session?.IsOpen == true) throw new ServiceException("waiting_already_open", "يوجد توقف مفتوح بالفعل.");
                var location = await RequireStationaryLocationAsync(ride, now, ct);
                session = new RideWaitingSession
                {
                    RideId = ride.Id, RequestedByUserId = id, RequestKey = m.RequestKey!.Value,
                    CreatedAtUtc = now, StartedAtUtc = now, FreeMinutes = ride.WaitingFreeMinutes,
                    PricePerMinute = ride.WaitingPricePerMinute, MaxFee = Math.Max(0, ride.WaitingMaxFee - ride.WaitingFee),
                    Latitude = location.Latitude, Longitude = location.Longitude
                };
                db.RideWaitingSessions.Add(session);
                waiting.Notify(ride, "بدأ السائق التوقف والانتظار",
                    $"بدأ عدّ الانتظار الآن. المهلة المجانية {session.FreeMinutes} دقيقة، ثم {session.PricePerMinute:0.##} ر.ي للدقيقة. السقف المتبقي {session.MaxFee:0} ر.ي.");
                ride.UpdatedAtUtc = now;
                changed = true;
            }
        }
        else if (operation != "get")
        {
            var requested = await db.RideWaitingSessions.SingleOrDefaultAsync(x => x.Id == m.SessionId && x.RideId == ride.Id, ct)
                ?? throw new ServiceException("waiting_not_found", "التوقف غير موجود.");
            var requests = RideWaitingResumeRequests.Read(requested);
            if (operation == "request_resume")
            {
                RequireRequestKey(m.RequestKey);
                if (!requests.Any(x => x.RequestKey == m.RequestKey))
                {
                    EnsureActiveStop(ride, requested);
                    if (requests.Any(x => x.Decision is null))
                        throw new ServiceException("waiting_resume_already_pending", "طلب الاستئناف السابق ينتظر قرار السائق.");
                    if (requests.Count >= RideWaitingResumeRequests.MaximumRequestsPerStop)
                        throw new ServiceException("waiting_resume_request_limit", "وصل هذا التوقف إلى حد طلبات الاستئناف؛ يمكن للسائق الاستئناف مباشرةً.");
                    requests.Add(new(m.RequestKey!.Value, id, now));
                    RideWaitingResumeRequests.Write(requested, requests);
                    requested.UpdatedAtUtc = now;
                    ride.UpdatedAtUtc = now;
                    waiting.Notify(ride, "طلب الراكب استئناف الرحلة", "طلب الراكب مواصلة الرحلة. يستمر عدّ الانتظار حتى يقبل السائق الطلب أو يستأنف مباشرةً.", "review_waiting", requested.Id);
                    changed = true;
                }
            }
            else if (operation == "resume")
            {
                if (requested.IsOpen)
                {
                    EnsureActiveStop(ride, requested);
                    RideWaitingResumeRequests.FinishPending(requested, now, "accepted", id);
                    waiting.Close(ride, requested, now, "driver_resumed");
                    changed = true;
                }
            }
            else
            {
                if (m.ResumeRequestKey is null || m.ResumeRequestKey == Guid.Empty)
                    throw new ServiceException("waiting_resume_request_required", "معرف طلب الاستئناف مطلوب لاتخاذ القرار.");
                var latest = requests.LastOrDefault();
                if (latest is null || latest.RequestKey != m.ResumeRequestKey)
                    throw new ServiceException("waiting_resume_request_mismatch", "تغير طلب الاستئناف؛ حدّث الرحلة قبل اتخاذ القرار.");
                var decision = operation == "accept_resume" ? "accepted" : "rejected";
                if (latest.Decision != decision)
                {
                    EnsureActiveStop(ride, requested);
                    if (latest.Decision is not null)
                        throw new ServiceException("waiting_resume_request_not_pending", "سبق اتخاذ قرار بشأن طلب الاستئناف.");
                    RideWaitingResumeRequests.FinishPending(requested, now, decision, id);
                    requested.UpdatedAtUtc = now;
                    ride.UpdatedAtUtc = now;
                    if (decision == "accepted") waiting.Close(ride, requested, now, "rider_resume_accepted");
                    else waiting.Notify(ride, "لم يقبل السائق طلب الاستئناف", "يستمر التوقف وعدّ الانتظار. يستطيع السائق استئناف الرحلة مباشرةً.", "open_active_ride", requested.Id);
                    changed = true;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        var snapshot = await SnapshotAsync(ride, id, ct);
        await transaction.CommitAsync(ct);
        if (changed) await waiting.PublishAsync(ride, ct);
        return snapshot;
    }

    private static void RequireRequestKey(Guid? key)
    {
        if (key is null || key == Guid.Empty) throw new ServiceException("request_key_required", "معرف الطلب مطلوب.");
    }

    private static void EnsureActiveStop(RideEntity ride, RideWaitingSession session)
    {
        if (!session.IsOpen || session.StartedAtUtc is null || ride.Status != RideStatus.InProgress || ride.ServiceEndedAtUtc is not null)
            throw new ServiceException("waiting_not_active", "انتهى التوقف أو الرحلة؛ حدّث حالة الرحلة.");
    }

    private async Task EnsureCanWaitAsync(RideEntity ride, CancellationToken ct)
    {
        await RideRouteChangePolicy.EnsureNoOpenAsync(db, ride.Id, ct);
        if (ride.Status != RideStatus.InProgress || ride.ServiceEndedAtUtc is not null || ride.DriverId is null || ride.CustomerPaymentEnabled)
            throw new ServiceException("waiting_not_available", "الانتظار متاح أثناء الرحلة وقبل فتح الدفع فقط.");
        if (ride.WaitingPricePerMinute <= 0 || ride.WaitingMaxFee <= ride.WaitingFee)
            throw new ServiceException("waiting_not_configured", "الانتظار المدفوع غير مفعّل لهذه الرحلة أو وصل إلى سقف الرسوم.");
        if (await HasPaymentStartedAsync(ride.Id, ct))
            throw new ServiceException("payment_already_open", "لا يمكن إضافة انتظار بعد فتح طلب تحصيل.");
    }

    private async Task<bool> HasPaymentStartedAsync(int rideId, CancellationToken ct) =>
        await db.PaymentWalletOperations.AnyAsync(x => x.RideId == rideId && x.IsOpen, ct) ||
        await db.PaymentTransactions.AnyAsync(x => x.RideId == rideId && (x.Status == PaymentStatus.Pending || x.Status == PaymentStatus.Authorized), ct) ||
        await db.CashPaymentRequests.AnyAsync(x => x.RideId == rideId && (x.Status == CashPaymentRequestStatus.Pending || x.Status == CashPaymentRequestStatus.DriverConfirmed), ct) ||
        await db.CashCollectionApprovals.AnyAsync(x => x.RideId == rideId && (x.Status == CashCollectionApprovalStatus.Pending || x.Status == CashCollectionApprovalStatus.Approved), ct);

    private static bool Stationary(DriverLiveLocation? location, DateTime now) => location is not null && location.IsOnline &&
        location.ObservedAtUtc >= now.AddSeconds(-30) && location.Speed is not null and <= 1.5;

    private async Task<DriverLiveLocation> RequireStationaryLocationAsync(RideEntity ride, DateTime now, CancellationToken ct)
    {
        var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, ct);
        if (!Stationary(location, now))
            throw new ServiceException("waiting_location_required", "يلزم موقع حديث للسائق والسيارة متوقفة قبل بدء الانتظار.");
        return location!;
    }

    private async Task<object> SnapshotAsync(RideEntity ride, int userId, CancellationToken ct)
    {
        var now = UtcNow;
        var history = await db.RideWaitingSessions.AsNoTracking().Where(x => x.RideId == ride.Id).OrderByDescending(x => x.Id).Take(50).ToListAsync(ct);
        var open = history.FirstOrDefault(x => x.IsOpen);
        var session = open ?? history.FirstOrDefault();
        var requests = session is null ? [] : RideWaitingResumeRequests.Read(session);
        var latestResumeRequest = requests.LastOrDefault();
        var active = open is { StartedAtUtc: not null } && ride.Status == RideStatus.InProgress && ride.ServiceEndedAtUtc is null;
        var pending = active && latestResumeRequest is { Decision: null };
        var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, ct);
        var activeFee = open is null ? 0 : RideWaitingCoordinator.FeeAt(open, now);
        var fare = ride.CustomerPrice ?? ride.ServerPrice ?? 0;
        var paymentStarted = await HasPaymentStartedAsync(ride.Id, ct);
        var routeChangeOpen = await db.RideRouteChanges.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct);
        var canStart = userId == ride.DriverId && ride.Status == RideStatus.InProgress && ride.ServiceEndedAtUtc is null &&
            !ride.CustomerPaymentEnabled && !paymentStarted && !routeChangeOpen && open is null &&
            ride.WaitingPricePerMinute > 0 && ride.WaitingFee < ride.WaitingMaxFee && Stationary(location, now);
        var canDecideResume = active && pending && userId == ride.DriverId;
        return new
        {
            rideId = ride.Id, serverNowUtc = now, ride.Status, controlMode = "driver",
            ride.WaitingFreeMinutes, ride.WaitingPricePerMinute, ride.WaitingMaxFee,
            settledWaitingFee = ride.WaitingFee, waitingFee = ride.WaitingFee + activeFee,
            baseFare = fare, ride.ServiceFee, totalAmount = fare + ride.ServiceFee + ride.WaitingFee + activeFee,
            connectionGraceSeconds = RideWaitingCoordinator.ConnectionGraceSeconds,
            connectionDeadlineUtc = location?.ObservedAtUtc.AddSeconds(RideWaitingCoordinator.ConnectionGraceSeconds),
            waitingStarted = active, resumeRequestPending = pending,
            canStart, canResume = active && userId == ride.DriverId,
            canRequestResume = active && !pending && userId == ride.CustomerId && requests.Count < RideWaitingResumeRequests.MaximumRequestsPerStop,
            canDecideResume, canRequest = canStart, canDecide = canDecideResume,
            requestedByMe = pending ? latestResumeRequest?.RequestedByUserId == userId : open?.RequestedByUserId == userId,
            session = session is null ? null : new
            {
                session.Id, session.IsOpen, session.RequestedByUserId, session.ApprovedByUserId,
                session.CreatedAtUtc, session.StartedAtUtc, session.EndedAtUtc, session.EndReason,
                session.FreeMinutes, session.PricePerMinute, session.MaxFee, session.Fee,
                resumeRequestPending = pending, resumeRequest = latestResumeRequest
            },
            history = history.Select(x => new
            {
                x.Id, x.RequestedByUserId, x.ApprovedByUserId, x.CreatedAtUtc, x.StartedAtUtc, x.EndedAtUtc, x.EndReason,
                x.FreeMinutes, x.PricePerMinute, x.MaxFee, x.Fee, resumeRequests = RideWaitingResumeRequests.Read(x)
            }).ToList()
        };
    }
}
