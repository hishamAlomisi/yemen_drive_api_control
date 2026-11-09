using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api.Tracking;

/// <summary>Visual transport has its own scoped, bounded workers, separate from durable state delivery.</summary>
public sealed class DriverLocationVisualWorker(IServiceScopeFactory scopes, DriverLocationVisualQueue queue,
    ILogger<DriverLocationVisualWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => !queue.Enabled ? Task.CompletedTask :
        Task.WhenAll(Enumerable.Range(0, queue.Workers).Select(_ => RunWorkerAsync(stoppingToken)));

    private async Task RunWorkerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!queue.TryBegin(out var update))
            {
                try { await queue.WaitAsync(token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                continue;
            }
            var successful = false; var stale = false;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(queue.DeliveryTimeout);
                stale = DateTime.UtcNow - update.Location.ObservedAtUtc > TimeSpan.FromSeconds(30);
                if (!stale)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    stale = !await scope.ServiceProvider.GetRequiredService<IDriverLocationVisualTransport>().SendAsync(update, deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    successful = !stale;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!token.IsCancellationRequested)
                    logger.LogDebug(error, "Discarded a delayed visual update for driver {DriverId}; its route remains durable.", update.Location.DriverId);
            }
            finally { queue.Complete(update.Location.DriverId, successful, stale); }
        }
    }

}

public interface IDriverLocationVisualTransport
{
    Task<bool> SendAsync(DriverVisualUpdate update, CancellationToken token);
}

public sealed class DriverLocationVisualTransport(YemenDriveDbContext db, IRideRealtimePublisher publisher,
    IHubContext<TrackingHub> hub, NearbyDriverSubscriptionStore nearby) : IDriverLocationVisualTransport
{
    private static readonly RideStatus[] TrackableStatuses = [RideStatus.DriverAssigned, RideStatus.DriverEnRoute,
        RideStatus.DriverArrived, RideStatus.InProgress, RideStatus.CancellationPending];
    public async Task<bool> SendAsync(DriverVisualUpdate update, CancellationToken token)
    {
        // No producer DbContext, closure, or request scope survives in the queue.
        var point = update.Location;
        // Availability or account access may have changed while this sample was
        // waiting. A cached producer profile must not resurrect a nearby marker.
        var current = await db.DriverProfiles.AsNoTracking().Where(x => x.UserId == point.DriverId &&
                x.User.IsActive && x.User.Role == UserRole.Driver)
            .Select(x => new { x.ServiceKindId, x.ServiceCatalogItemId,
                Available = x.IsAvailable && x.AcceptsNewRequests && x.IsApproved,
                HasActiveRide = db.Rides.Any(ride => ride.DriverId == point.DriverId &&
                    ride.ServiceEndedAtUtc == null && TrackableStatuses.Contains(ride.Status)) })
            .SingleOrDefaultAsync(token);
        var applicable = current is not null;
        if (point.RideId is int rideId)
        {
            var ride = await db.Rides.AsNoTracking().Where(x => x.Id == rideId && x.DriverId == point.DriverId &&
                x.Driver!.IsActive && x.ServiceEndedAtUtc == null && TrackableStatuses.Contains(x.Status))
                .Select(x => new { x.CustomerId, x.Status }).SingleOrDefaultAsync(token);
            applicable &= ride is not null;
            if (applicable)
                await publisher.PublishDriverLocationAsync(point with { CustomerId = ride!.CustomerId, RideStatus = ride.Status }, token);
        }
        var profile = update.Profile;
        var available = current is not null && current.Available && !current.HasActiveRide && point.RideId is null &&
            current.ServiceKindId == profile.ServiceKindId && current.ServiceCatalogItemId == profile.ServiceCatalogItemId;
        var deliveries = nearby.Evaluate(profile.ServiceKindId, profile.ServiceCatalogItemId, point.DriverId,
            point.Latitude, point.Longitude, available);
        await Parallel.ForEachAsync(deliveries, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (delivery, ct) =>
        {
            if (!delivery.IsAvailable)
            {
                await hub.Clients.Client(delivery.ConnectionId).SendAsync("nearbyDriverAvailabilityChanged", new { driverId = point.DriverId, isAvailable = false }, ct);
                return;
            }
            await hub.Clients.Client(delivery.ConnectionId).SendAsync("nearbyDriverLocationUpdated", new {
                driverId = point.DriverId, name = profile.Name ?? "سائق يمن درايف", photoUrl = profile.PhotoUrl,
                vehicleModel = profile.VehicleModel, plateNumber = profile.PlateNumber, rating = profile.Rating,
                serviceCode = profile.ServiceCode, latitude = point.Latitude, longitude = point.Longitude,
                bearing = point.Bearing, observedAtUtc = point.ObservedAtUtc, distanceMeters = delivery.DistanceMeters, isAvailable = true
            }, ct);
        });
        return applicable;
    }
}
