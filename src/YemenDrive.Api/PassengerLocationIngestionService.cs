using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

/// <summary>Shares passenger GPS validation and persistence between Hub and HTTP delivery.</summary>
public sealed class PassengerLocationIngestionService(YemenDriveDbContext db)
{
    private static readonly RideStatus[] TrackableStatuses = [RideStatus.DriverAssigned,
        RideStatus.DriverEnRoute, RideStatus.DriverArrived, RideStatus.InProgress, RideStatus.CancellationPending];

    public async Task<PassengerLocationPublication?> SaveAsync(int customerId,
        PassengerLocationHubRequest sample, CancellationToken token)
    {
        if (sample.RideId <= 0 ||
            !double.IsFinite(sample.Latitude) || !double.IsFinite(sample.Longitude) ||
            sample.Latitude is < -90 or > 90 || sample.Longitude is < -180 or > 180 ||
            !double.IsFinite(sample.Accuracy) || sample.Accuracy is <= 0 or > 75 ||
            sample.Bearing is double bearing && (!double.IsFinite(bearing) || bearing is < 0 or >= 360) ||
            sample.Speed is double speed && (!double.IsFinite(speed) || speed is < 0 or > 75))
            throw new HubException("بيانات موقع العميل غير صالحة أو دقتها منخفضة.");
        var now = DateTime.UtcNow;
        var observedAtUtc = sample.ObservedAtUtc?.ToUniversalTime() ?? now;
        if (observedAtUtc < now.AddSeconds(-30) || observedAtUtc > now.AddSeconds(5))
            throw new HubException("عينة موقع العميل قديمة أو وقتها غير صالح.");

        // Both transports may overlap when a Hub reply is lost. Serialize their
        // replay check and write so the same observation has one history row.
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var resource = $"passenger-route:{customerId}:{sample.RideId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51003, 'Passenger sample lock timed out', 1;", token);
        var ride = await db.Rides.AsNoTracking()
            .Where(item => item.Id == sample.RideId && item.CustomerId == customerId &&
                item.Customer.IsActive && item.Customer.Role == UserRole.Customer && item.DriverId != null &&
                item.ServiceEndedAtUtc == null && TrackableStatuses.Contains(item.Status))
            .Select(item => new { item.DriverId, item.Status }).SingleOrDefaultAsync(token);
        if (ride?.DriverId is not int driverId)
            throw new HubException("الرحلة غير مخصصة لك أو لم تعد قابلة للتتبع.");
        var previous = await db.LocationUpdates.AsNoTracking()
            .Where(item => item.RideId == sample.RideId && item.ActorId == customerId)
            .OrderByDescending(item => item.ObservedAtUtc)
            .Select(item => new { item.Latitude, item.Longitude, item.ObservedAtUtc }).FirstOrDefaultAsync(token);
        if (previous is not null)
        {
            if (observedAtUtc <= previous.ObservedAtUtc ||
                observedAtUtc - previous.ObservedAtUtc < TimeSpan.FromMilliseconds(750)) return null;
            var metres = HaversineMeters(previous.Latitude, previous.Longitude, sample.Latitude, sample.Longitude);
            if (!double.IsFinite(metres) || metres > 75 * (observedAtUtc - previous.ObservedAtUtc).TotalSeconds +
                Math.Max(50, sample.Accuracy * 2))
                throw new HubException("قفزة موقع العميل لا تتوافق مع سرعة حركة ممكنة.");
        }
        db.LocationUpdates.Add(new LocationUpdate { RideId = sample.RideId, ActorId = customerId,
            Latitude = sample.Latitude, Longitude = sample.Longitude, Bearing = sample.Bearing,
            Speed = sample.Speed, Accuracy = sample.Accuracy, ObservedAtUtc = observedAtUtc });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(driverId, new PassengerLocationPayload(sample.RideId, customerId, sample.Latitude,
            sample.Longitude, sample.Bearing, sample.Speed, sample.Accuracy, observedAtUtc, now, (int)ride.Status));
    }

    internal static async Task PublishAsync(IHubClients<IClientProxy> clients, PassengerLocationPublication? publication,
        ILogger logger, CancellationToken token)
    {
        if (publication is null) return;
        // Passenger coordinates are private to the assigned driver and operations.
        foreach (var group in new[] { TrackingHub.UserGroup(publication.DriverId), TrackingHub.OperationsGroup })
        {
            try { await clients.Group(group).SendAsync("passengerLocationUpdated", publication.Payload, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning(exception,
                "Could not publish passengerLocationUpdated for ride {RideId}.", publication.Payload.RideId); }
        }
    }

    private static double HaversineMeters(double firstLat, double firstLon, double secondLat, double secondLon)
    {
        var lat = (secondLat - firstLat) * Math.PI / 180;
        var lon = (secondLon - firstLon) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(lat / 2), 2) + Math.Cos(firstLat * Math.PI / 180) *
            Math.Cos(secondLat * Math.PI / 180) * Math.Pow(Math.Sin(lon / 2), 2);
        a = Math.Clamp(a, 0, 1);
        return 2 * 6_371_000 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

public sealed record PassengerLocationPayload(int RideId, int CustomerId, double Latitude, double Longitude,
    double? Bearing, double? Speed, double Accuracy, DateTime ObservedAtUtc, DateTime ReceivedAtUtc, int RideStatus);
public sealed record PassengerLocationPublication(int DriverId, PassengerLocationPayload Payload);
