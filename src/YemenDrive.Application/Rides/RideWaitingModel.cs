using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public sealed record RideWaitingModel(int? RideId = null, int? SessionId = null, Guid? RequestKey = null);

public sealed class RideWaiting(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext user,
    RideWaitingCoordinator waiting) : OperationsService<RideWaitingModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get", "add", "accept", "reject", "cancel", "update"];
    protected override Task<object?> GetAsync(RideWaitingModel m, CancellationToken ct) => RunAsync(m, "get", ct);
    protected override Task<object?> AddAsync(RideWaitingModel m, CancellationToken ct) => RunAsync(m, "add", ct);
    protected override Task<object?> AcceptAsync(RideWaitingModel m, CancellationToken ct) => RunAsync(m, "accept", ct);
    protected override Task<object?> RejectAsync(RideWaitingModel m, CancellationToken ct) => RunAsync(m, "reject", ct);
    protected override Task<object?> CancelAsync(RideWaitingModel m, CancellationToken ct) => RunAsync(m, "cancel", ct);
    protected override Task<object?> UpdateAsync(RideWaitingModel m, CancellationToken ct) => RunAsync(m, "update", ct);

    private async Task<object?> RunAsync(RideWaitingModel m, string operation, CancellationToken ct)
    {
        var id = user.UserId ?? throw new ServiceException("authentication_required", "سجّل الدخول لإدارة الانتظار.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (m.RideId is int rideId) await RidePaymentGuard.LockAsync(db, rideId, ct);
        var ride = await db.Rides.SingleOrDefaultAsync(x => x.Id == m.RideId, ct)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        var participant = id == ride.CustomerId || id == ride.DriverId;
        var admin = !participant && operation == "get" && await db.Users.AnyAsync(x => x.Id == id && x.IsActive && x.Role == UserRole.Admin, ct);
        if (!participant && !admin) throw new ServiceException("ride_access_denied", "هذه الرحلة لا تخصك.");
        var now = DateTime.UtcNow;
        var session = await waiting.ReconcileAsync(ride, now, ct);
        var changed = db.ChangeTracker.HasChanges();
        // Release the filtered open-session index before inserting a new stop
        // in this same transaction (for example after a timed-out request).
        if (changed) await db.SaveChangesAsync(ct);
        if (operation != "get")
        {
            if (operation == "add")
            {
                if (m.RequestKey is null || m.RequestKey == Guid.Empty)
                    throw new ServiceException("request_key_required", "معرف طلب الانتظار مطلوب.");
                var existing = await db.RideWaitingSessions.AsNoTracking().SingleOrDefaultAsync(x => x.RideId == ride.Id && x.RequestKey == m.RequestKey, ct);
                if (existing is null)
                {
                    await EnsureCanWaitAsync(ride, ct);
                    if (session?.IsOpen == true) throw new ServiceException("waiting_already_open", "يوجد طلب انتظار أو توقف مفتوح بالفعل.");
                    var location = await RequireStationaryLocationAsync(ride, now, ct);
                    session = new RideWaitingSession
                    {
                        RideId = ride.Id, RequestedByUserId = id, RequestKey = m.RequestKey.Value,
                        CreatedAtUtc = now, FreeMinutes = ride.WaitingFreeMinutes,
                        PricePerMinute = ride.WaitingPricePerMinute, MaxFee = Math.Max(0, ride.WaitingMaxFee - ride.WaitingFee),
                        Latitude = location.Latitude, Longitude = location.Longitude
                    };
                    db.RideWaitingSessions.Add(session);
                    waiting.Notify(ride, "طلب توقف أثناء الرحلة", "راجع مهلة الانتظار المجانية ورسوم الدقيقة والسقف ثم وافق أو ارفض. لم يبدأ احتساب رسوم بعد.", action: "review_waiting");
                    changed = true;
                }
            }
            else
            {
                var requested = await db.RideWaitingSessions.SingleOrDefaultAsync(x => x.Id == m.SessionId && x.RideId == ride.Id, ct)
                    ?? throw new ServiceException("waiting_not_found", "طلب الانتظار غير موجود.");
                if (requested.IsOpen)
                {
                    if (operation == "accept")
                    {
                        if (requested.RequestedByUserId == id) throw new ServiceException("waiting_other_party_required", "تحتاج موافقة الطرف الآخر.");
                        if (requested.StartedAtUtc is null)
                        {
                            await EnsureCanWaitAsync(ride, ct);
                            var location = await RequireStationaryLocationAsync(ride, now, ct);
                            requested.ApprovedByUserId = id;
                            requested.StartedAtUtc = now;
                            requested.Latitude = location.Latitude;
                            requested.Longitude = location.Longitude;
                            waiting.Notify(ride, "بدأ الانتظار بموافقة الطرفين", $"المهلة المجانية {requested.FreeMinutes} دقيقة، ثم {requested.PricePerMinute:0.##} ر.ي للدقيقة. السقف المتبقي {requested.MaxFee:0} ر.ي.");
                        }
                    }
                    else if (operation is "reject" or "cancel")
                    {
                        if (requested.StartedAtUtc is not null) throw new ServiceException("waiting_already_started", "بدأ الانتظار؛ استخدم استئناف الرحلة لإيقافه.");
                        if (operation == "cancel" && requested.RequestedByUserId != id || operation == "reject" && requested.RequestedByUserId == id)
                            throw new ServiceException("waiting_access_denied", "لا يمكنك تنفيذ هذا القرار.");
                        waiting.Close(ride, requested, now, operation == "cancel" ? "withdrawn" : "rejected");
                    }
                    else
                    {
                        if (ride.DriverId != id) throw new ServiceException("driver_action_required", "استئناف الرحلة متاح للسائق المسند فقط.");
                        waiting.Close(ride, requested, now, "driver_resumed");
                    }
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

    private async Task EnsureCanWaitAsync(RideEntity ride, CancellationToken ct)
    {
        await RideRouteChangePolicy.EnsureNoOpenAsync(db, ride.Id, ct);
        if (ride.Status != RideStatus.InProgress || ride.DriverId is null || ride.CustomerPaymentEnabled)
            throw new ServiceException("waiting_not_available", "الانتظار متاح أثناء الرحلة وقبل فتح الدفع فقط.");
        if (ride.WaitingPricePerMinute <= 0 || ride.WaitingMaxFee <= ride.WaitingFee)
            throw new ServiceException("waiting_not_configured", "الانتظار المدفوع غير مفعّل لهذه الرحلة أو وصل إلى سقف الرسوم.");
        if (await db.PaymentWalletOperations.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct) ||
            await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && (x.Status == PaymentStatus.Pending || x.Status == PaymentStatus.Paid), ct) ||
            await db.CashCollectionApprovals.AnyAsync(x => x.RideId == ride.Id && (x.Status == CashCollectionApprovalStatus.Pending || x.Status == CashCollectionApprovalStatus.Approved), ct))
            throw new ServiceException("payment_already_open", "لا يمكن إضافة انتظار بعد فتح طلب تحصيل.");
    }

    private async Task<DriverLiveLocation> RequireStationaryLocationAsync(RideEntity ride, DateTime now, CancellationToken ct)
    {
        var location = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, ct);
        if (location is null || !location.IsOnline || location.ObservedAtUtc < now.AddSeconds(-30) || location.Speed is null or > 1.5)
            throw new ServiceException("waiting_location_required", "يلزم موقع حديث للسائق والسيارة متوقفة قبل بدء الانتظار.");
        return location;
    }

    private async Task<object> SnapshotAsync(RideEntity ride, int userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var history = await db.RideWaitingSessions.AsNoTracking().Where(x => x.RideId == ride.Id).OrderByDescending(x => x.Id).Take(50).ToListAsync(ct);
        var open = history.FirstOrDefault(x => x.IsOpen);
        var session = open ?? history.FirstOrDefault();
        var locationTime = await db.DriverLiveLocations.AsNoTracking().Where(x => x.DriverId == ride.DriverId).Select(x => (DateTime?)x.ObservedAtUtc).FirstOrDefaultAsync(ct);
        var activeFee = open is null ? 0 : RideWaitingCoordinator.FeeAt(open, now);
        var fare = ride.CustomerPrice ?? ride.ServerPrice ?? 0;
        var walletPaymentOpen = await db.PaymentWalletOperations.AsNoTracking()
            .AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct);
        return new
        {
            rideId = ride.Id, serverNowUtc = now, ride.Status,
            ride.WaitingFreeMinutes, ride.WaitingPricePerMinute, ride.WaitingMaxFee,
            settledWaitingFee = ride.WaitingFee, waitingFee = ride.WaitingFee + activeFee,
            baseFare = fare, ride.ServiceFee, totalAmount = fare + ride.ServiceFee + ride.WaitingFee + activeFee,
            connectionGraceSeconds = RideWaitingCoordinator.ConnectionGraceSeconds,
            connectionDeadlineUtc = locationTime?.AddSeconds(RideWaitingCoordinator.ConnectionGraceSeconds),
            canRequest = ride.Status == RideStatus.InProgress && !ride.CustomerPaymentEnabled && !walletPaymentOpen && open is null && ride.WaitingPricePerMinute > 0 && ride.WaitingFee < ride.WaitingMaxFee,
            canDecide = open is { StartedAtUtc: null } && open.RequestedByUserId != userId,
            requestedByMe = open?.RequestedByUserId == userId,
            session = session is null ? null : new { session.Id, session.IsOpen, session.StartedAtUtc, session.EndedAtUtc, session.EndReason, session.FreeMinutes, session.PricePerMinute, session.MaxFee, session.Fee },
            history = history.Select(x => new { x.Id, x.RequestedByUserId, x.ApprovedByUserId, x.CreatedAtUtc, x.StartedAtUtc, x.EndedAtUtc, x.EndReason, x.FreeMinutes, x.PricePerMinute, x.MaxFee, x.Fee })
        };
    }
}
