using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api.Tracking;

/// <summary>Receipts and the pricing archive commit together; map publication is expendable.</summary>
public sealed class RouteSampleBatchService(YemenDriveDbContext db, TrackingUpgradeSettings settings,
    DriverLocationIngestionService liveLocations, ILogger<RouteSampleBatchService> logger)
{
    public async Task<RouteSampleBatchResult> SaveAsync(int driverId, RouteSampleBatchRequest request, CancellationToken ct)
    {
        if (!settings.DurableSamplesEnabled) return new(false, [], 0, false);
        if (driverId <= 0 || request.RideId <= 0 || request.SessionId == Guid.Empty ||
            request.Samples is not { Length: > 0 and <= 64 } ||
            request.Samples.Select(x => x.SampleId).Distinct().Count() != request.Samples.Length ||
            request.Samples.Select(x => x.Sequence).Distinct().Count() != request.Samples.Length)
            throw new HubException("دفعة الموقع غير صالحة؛ الحد الأقصى 64 عينة فريدة.");
        var now = DateTime.UtcNow;
        var ordered = request.Samples.OrderBy(x => x.Sequence).ToArray();
        foreach (var sample in ordered) Validate(sample, now);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The SQL lock coordinates retrying requests, even across API processes.
        // This feature does not by itself enable multi-instance SignalR deployment.
        var resource = $"driver-route:{driverId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51002, 'Route sample lock timed out', 1;", ct);
        if (!await db.DriverProfiles.AsNoTracking().AnyAsync(x => x.UserId == driverId &&
                x.User.IsActive && x.User.Role == UserRole.Driver, ct))
            throw new HubException("الحساب لا يملك ملف سائق نشطًا.");
        var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.RideId && x.DriverId == driverId, ct);
        if (ride is null) throw new HubException("الرحلة غير مسندة إليك.");
        var end = ride.ServiceEndedAtUtc ?? ride.CompletedAtUtc;
        if (end is null && ride.Status is not (RideStatus.DriverAssigned or RideStatus.DriverEnRoute or
                RideStatus.DriverArrived or RideStatus.InProgress or RideStatus.CancellationPending))
            throw new HubException("الرحلة غير قابلة لتسجيل المواقع.");
        var session = await db.DriverRouteSessions.SingleOrDefaultAsync(x => x.DriverId == driverId && x.SessionId == request.SessionId, ct);
        if (session is null)
        {
            session = new DriverRouteSession { DriverId = driverId, RideId = ride.Id, SessionId = request.SessionId };
            db.DriverRouteSessions.Add(session);
        }
        if (session.RideId != ride.Id) throw new HubException("جلسة التتبع مرتبطة برحلة أخرى.");
        var ids = ordered.Select(x => x.SampleId).ToArray();
        var sequences = ordered.Select(x => x.Sequence).ToArray();
        var existing = await db.DriverRouteSamples.AsNoTracking().Where(x => x.DriverId == driverId &&
            (ids.Contains(x.SampleId) || x.SessionId == request.SessionId && sequences.Contains(x.Sequence))).ToListAsync(ct);
        var additions = new List<DriverRouteSample>();
        foreach (var sample in ordered)
        {
            var old = existing.SingleOrDefault(x => x.SampleId == sample.SampleId);
            if (old is not null)
            {
                if (!Matches(old, request, sample)) throw new HubException("لا يمكن تغيير عينة موقع سبق تأكيدها.");
                continue;
            }
            if (existing.Any(x => x.SessionId == request.SessionId && x.Sequence == sample.Sequence))
                throw new HubException("تسلسل العينة مستخدم لهوية مختلفة.");
            if (sample.ObservedAtUtc.ToUniversalTime() < now.AddHours(-24) ||
                sample.ObservedAtUtc.ToUniversalTime() < ride.CreatedAtUtc ||
                end is DateTime ended && sample.ObservedAtUtc.ToUniversalTime() > ended)
                throw new HubException("وقت العينة خارج حدود هذه الرحلة أو تجاوز مهلة الاستعادة 24 ساعة.");
            additions.Add(new DriverRouteSample { DriverId = driverId, RideId = ride.Id, SessionId = request.SessionId,
                SampleId = sample.SampleId, Sequence = sample.Sequence, Latitude = sample.Latitude, Longitude = sample.Longitude,
                Bearing = sample.Bearing, Speed = sample.Speed, Accuracy = sample.Accuracy,
                ObservedAtUtc = sample.ObservedAtUtc.ToUniversalTime(), CreatedAtUtc = now, UpdatedAtUtc = now });
        }
        // Inspect neighbouring committed samples as well as the new batch. Backfilling
        // a gap must satisfy both neighbours, not just the current map position.
        var minimumSequence = ordered[0].Sequence;
        var maximumSequence = ordered[^1].Sequence;
        var range = await db.DriverRouteSamples.AsNoTracking().Where(x => x.DriverId == driverId &&
            x.SessionId == request.SessionId && x.Sequence >= minimumSequence && x.Sequence <= maximumSequence).ToListAsync(ct);
        var before = await db.DriverRouteSamples.AsNoTracking().Where(x => x.DriverId == driverId &&
            x.SessionId == request.SessionId && x.Sequence < minimumSequence).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        var after = await db.DriverRouteSamples.AsNoTracking().Where(x => x.DriverId == driverId &&
            x.SessionId == request.SessionId && x.Sequence > maximumSequence).OrderBy(x => x.Sequence).FirstOrDefaultAsync(ct);
        var neighbours = range.Concat(additions).Concat(before is null ? [] : new[] { before })
            .Concat(after is null ? [] : new[] { after }).OrderBy(x => x.Sequence).ToArray();
        for (var i = 1; i < neighbours.Length; i++) CheckMovement(neighbours[i - 1], neighbours[i]);

        var start = ordered.Min(x => x.ObservedAtUtc.ToUniversalTime());
        var finish = ordered.Max(x => x.ObservedAtUtc.ToUniversalTime());
        var existingArchive = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == ride.Id && x.ActorId == driverId &&
            x.ObservedAtUtc >= start && x.ObservedAtUtc <= finish).ToListAsync(ct);
        if (additions.Count > 0)
        {
            var archiveBefore = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == ride.Id && x.ActorId == driverId &&
                x.ObservedAtUtc < start).OrderByDescending(x => x.ObservedAtUtc).FirstOrDefaultAsync(ct);
            var archiveAfter = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == ride.Id && x.ActorId == driverId &&
                x.ObservedAtUtc > finish).OrderBy(x => x.ObservedAtUtc).FirstOrDefaultAsync(ct);
            var history = existingArchive.Concat(archiveBefore is null ? [] : new[] { archiveBefore })
                .Concat(archiveAfter is null ? [] : new[] { archiveAfter })
                .Select(x => (x.ObservedAtUtc, x.Latitude, x.Longitude, Accuracy: x.Accuracy ?? 75))
                .Concat(additions.Select(x => (x.ObservedAtUtc, x.Latitude, x.Longitude, x.Accuracy)))
                .OrderBy(x => x.ObservedAtUtc).ToArray();
            for (var i = 1; i < history.Length; i++)
            {
                var elapsed = (history[i].ObservedAtUtc - history[i - 1].ObservedAtUtc).TotalSeconds;
                var distance = Distance(history[i-1].Latitude, history[i-1].Longitude, history[i].Latitude, history[i].Longitude);
                if (elapsed == 0 && distance > 0.01 || distance > 75 * elapsed + Math.Max(50, Math.Max(history[i-1].Accuracy, history[i].Accuracy) * 2))
                    throw new HubException("العينة لا تتوافق مع سجل المسار المحفوظ.");
            }
        }
        foreach (var sample in additions)
        {
            db.DriverRouteSamples.Add(sample);
            // The legacy live path and start-of-route anchor may already have saved
            // this exact observation during an upgrade or lost response.
            if (!existingArchive.Any(x => x.ObservedAtUtc == sample.ObservedAtUtc &&
                    x.Latitude == sample.Latitude && x.Longitude == sample.Longitude))
                db.LocationUpdates.Add(new LocationUpdate { RideId = ride.Id, ActorId = driverId,
                    Latitude = sample.Latitude, Longitude = sample.Longitude, Bearing = sample.Bearing,
                    Speed = sample.Speed, Accuracy = sample.Accuracy, ObservedAtUtc = sample.ObservedAtUtc, Source = "gps" });
        }
        session.HighestSequence = Math.Max(session.HighestSequence, maximumSequence);
        session.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        while (session.LastContiguousSequence < session.HighestSequence)
        {
            var next = await db.DriverRouteSamples.AsNoTracking().Where(x => x.DriverId == driverId &&
                x.SessionId == request.SessionId && x.Sequence > session.LastContiguousSequence)
                .OrderBy(x => x.Sequence).Select(x => x.Sequence).Take(256).ToListAsync(ct);
            var previous = session.LastContiguousSequence;
            foreach (var sequence in next)
            {
                if (sequence != session.LastContiguousSequence + 1) break;
                session.LastContiguousSequence = sequence;
            }
            if (session.LastContiguousSequence == previous || next.Count < 256) break;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var latest = ordered.MaxBy(x => x.ObservedAtUtc)!;
        var livePublished = false;
        if (end is null && latest.ObservedAtUtc.ToUniversalTime() >= now.AddSeconds(-30))
        {
            try
            {
                livePublished = await liveLocations.SaveWithoutArchiveAsync(driverId, new(ride.Id, latest.Latitude, latest.Longitude,
                    latest.Bearing, latest.Speed, latest.Accuracy, latest.ObservedAtUtc));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A transient live failure must not turn a durable receipt into an
                // ambiguous acknowledgement. A newer sample will refresh the map.
                logger.LogWarning(ex, "Route samples are durable but live update failed for ride {RideId}.", ride.Id);
            }
        }
        return new(true, ordered.Select(x => x.SampleId).ToArray(), session.LastContiguousSequence, livePublished);
    }

    private static void Validate(RouteSampleRequest sample, DateTime now)
    {
        if (sample.SampleId == Guid.Empty || sample.Sequence is <= 0 or > 1_000_000_000 ||
            !double.IsFinite(sample.Latitude) || sample.Latitude is < -90 or > 90 ||
            !double.IsFinite(sample.Longitude) || sample.Longitude is < -180 or > 180 ||
            !double.IsFinite(sample.Accuracy) || sample.Accuracy is <= 0 or > 75 ||
            sample.Bearing is double bearing && (!double.IsFinite(bearing) || bearing is < 0 or >= 360) ||
            sample.Speed is double speed && (!double.IsFinite(speed) || speed is < 0 or > 75) ||
            sample.ObservedAtUtc.ToUniversalTime() > now.AddSeconds(5))
            throw new HubException("عينة الموقع أو توقيتها أو دقتها غير صالح؛ لم تُؤكد الدفعة.");
    }

    private static bool Matches(DriverRouteSample old, RouteSampleBatchRequest batch, RouteSampleRequest sample) =>
        old.RideId == batch.RideId && old.SessionId == batch.SessionId && old.Sequence == sample.Sequence &&
        old.Latitude == sample.Latitude && old.Longitude == sample.Longitude && old.Bearing == sample.Bearing &&
        old.Speed == sample.Speed && old.Accuracy == sample.Accuracy && old.ObservedAtUtc == sample.ObservedAtUtc.ToUniversalTime();

    private static void CheckMovement(DriverRouteSample before, DriverRouteSample after)
    {
        var elapsed = (after.ObservedAtUtc - before.ObservedAtUtc).TotalSeconds;
        if (elapsed <= 0 || Distance(before.Latitude, before.Longitude, after.Latitude, after.Longitude) >
                75 * elapsed + Math.Max(50, Math.Max(before.Accuracy, after.Accuracy) * 2))
            throw new HubException("تسلسل أو حركة الموقع لا يتوافق مع العينات المحفوظة.");
    }

    private static double Distance(double a, double b, double c, double d)
    {
        const double radians = Math.PI / 180;
        var value = Math.Pow(Math.Sin((c-a)*radians/2),2) + Math.Cos(a*radians)*Math.Cos(c*radians)*Math.Pow(Math.Sin((d-b)*radians/2),2);
        return 12_742_000 * Math.Asin(Math.Sqrt(Math.Clamp(value,0,1)));
    }
}
