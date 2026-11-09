using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Api.Tracking;
using YemenDrive.Application.Rides;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

public sealed partial class TrackingHub
{
    public Task<RouteSampleBatchResult> PublishDriverLocationBatch(RouteSampleBatchRequest request)
    {
        if (!TryGetAuthenticatedUserId(out var driverId)) throw new HubException("يجب تسجيل الدخول لإرسال المواقع.");
        return routeSamples?.SaveAsync(driverId, request, Context.ConnectionAborted)
            ?? Task.FromResult(new RouteSampleBatchResult(false, [], 0, false));
    }

    public async Task<RideMapWatchResult> WatchRideMap(int rideId)
    {
        if (!TryGetAuthenticatedUserId(out var userId) ||
            !await db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.IsActive, Context.ConnectionAborted) ||
            !await accessPolicy.CanJoinRideAsync(userId, rideId, Context.ConnectionAborted))
            throw new HubException("ليس لديك صلاحية متابعة خريطة هذه الرحلة.");
        var expires = DateTime.UtcNow.AddSeconds(90);
        if (mapWatches is null) return new(false, null, expires);
        var ride = await db.Rides.AsNoTracking().Where(x => x.Id == rideId)
            .Select(x => new { x.DriverId, x.CustomerId, x.Status, x.ServiceEndedAtUtc }).SingleAsync(Context.ConnectionAborted);
        if (ride.ServiceEndedAtUtc is not null || ride.Status is RideStatus.Completed or RideStatus.Cancelled)
            throw new HubException("انتهت المتابعة الحية لهذه الرحلة.");
        mapWatches.Watch(Context.ConnectionId, rideId, expires);
        // This subscription is independent of general business-state JoinRide.
        // The saved observation carries its true age even if it is already stale.
        DriverLocationRealtimeEvent? snapshot = null;
        if (ride.DriverId is int driverId)
        {
            var point = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == driverId &&
                x.Driver.IsActive, Context.ConnectionAborted);
            if (point is not null)
            {
                var accuracy = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == rideId && x.ActorId == driverId &&
                    x.ObservedAtUtc == point.ObservedAtUtc).Select(x => x.Accuracy).FirstOrDefaultAsync(Context.ConnectionAborted);
                snapshot = new(driverId, rideId, ride.CustomerId, point.Latitude, point.Longitude, point.Bearing,
                    point.Speed, accuracy ?? 75, DateTime.SpecifyKind(point.ObservedAtUtc, DateTimeKind.Utc),
                    DateTime.SpecifyKind(point.UpdatedAtUtc ?? point.ObservedAtUtc, DateTimeKind.Utc), ride.Status);
            }
            await Clients.Group(UserGroup(driverId)).SendAsync("trackingDemandChanged",
                new { rideId, hasViewers = true, expiresAtUtc = expires }, Context.ConnectionAborted);
        }
        return new(true, snapshot, expires);
    }

    public async Task LeaveRideMap(int rideId)
    {
        mapWatches?.Remove(Context.ConnectionId, rideId);
        if (!TryGetAuthenticatedUserId(out var userId) || mapWatches is null ||
            !await accessPolicy.CanJoinRideAsync(userId, rideId, Context.ConnectionAborted)) return;
        var driverId = await db.Rides.AsNoTracking().Where(x => x.Id == rideId).Select(x => x.DriverId).SingleAsync(Context.ConnectionAborted);
        if (driverId is int id)
            await Clients.Group(UserGroup(id)).SendAsync("trackingDemandChanged",
                new { rideId, hasViewers = mapWatches.HasWatchers(rideId), expiresAtUtc = DateTime.UtcNow.AddSeconds(90) }, Context.ConnectionAborted);
    }
}
