using System.Collections.Concurrent;
using YemenDrive.Application.Rides;

namespace YemenDrive.Api.Tracking;

public sealed record RideMapWatchResult(bool Enabled, DriverLocationRealtimeEvent? Location, DateTime ExpiresAtUtc);

/// <summary>Short leases express visible map interest, never authorization or pricing truth.</summary>
public sealed class RideMapWatchStore
{
    private readonly ConcurrentDictionary<(string Connection, int Ride), DateTime> watches = new();
    public void Watch(string connection, int rideId, DateTime expiresAtUtc)
    {
        RemoveExpired();
        watches[(connection, rideId)] = expiresAtUtc;
    }
    public void Remove(string connection, int? rideId = null)
    {
        foreach (var key in watches.Keys.Where(x => x.Connection == connection && (rideId is null || x.Ride == rideId)))
            watches.TryRemove(key, out _);
    }
    public bool HasWatchers(int rideId)
    {
        RemoveExpired();
        return watches.Any(x => x.Key.Ride == rideId);
    }
    private void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var item in watches.Where(x => x.Value <= now)) watches.TryRemove(item.Key, out _);
    }
}
