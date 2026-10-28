using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

/// <summary>Revalidates a ride invitation at delivery time, not just when queued.</summary>
public static class RideRequestNotificationPolicy
{
    public static int? RequestRideId(Notification notification)
    {
        if (notification.Type != NotificationType.RideOffer ||
            string.IsNullOrWhiteSpace(notification.DataJson) || notification.DataJson.Length > 4096)
            return null;
        try
        {
            using var json = JsonDocument.Parse(notification.DataJson);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.String || action.GetString() != "open_ride_offer" ||
                !root.TryGetProperty("rideId", out var id)) return null;
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number > 0)
                return number;
            return id.ValueKind == JsonValueKind.String && int.TryParse(id.GetString(), out number) && number > 0
                ? number : null;
        }
        catch (JsonException) { return null; }
    }

    public static async Task<bool> CanAlertAsync(YemenDriveDbContext db, Notification notification,
        CancellationToken token)
    {
        if (RequestRideId(notification) is not int rideId) return true;
        // A retry/outbox backlog must never turn into a delayed "new request" sound.
        if (notification.CreatedAtUtc < DateTime.UtcNow.AddSeconds(-60)) return false;
        return (await EligibleRideIdsAsync(db, notification.UserId, [rideId], token)).Contains(rideId);
    }

    public static async Task<HashSet<int>> EligibleRideIdsAsync(YemenDriveDbContext db, int driverId,
        IEnumerable<int> requestedIds, CancellationToken token)
    {
        var ids = requestedIds.Distinct().Where(id => id > 0).ToArray();
        if (ids.Length == 0) return [];
        var profile = await db.DriverProfiles.AsNoTracking()
            .Where(profile => profile.UserId == driverId && profile.IsAvailable &&
                profile.User.IsActive && profile.User.Role == UserRole.Driver &&
                !db.Rides.Any(active => active.DriverId == driverId &&
                    (active.Status == RideStatus.DriverAssigned || active.Status == RideStatus.DriverEnRoute ||
                     active.Status == RideStatus.DriverArrived || active.Status == RideStatus.InProgress ||
                     active.Status == RideStatus.CancellationPending)))
            .Select(profile => new { profile.ServiceKindId, profile.ServiceCatalogItemId })
            .SingleOrDefaultAsync(token);
        if (profile is null) return [];
        var now = DateTime.UtcNow;
        var freshAfter = now.AddMinutes(-5);
        var location = await db.DriverLiveLocations.AsNoTracking()
            .SingleOrDefaultAsync(location => location.DriverId == driverId && location.IsOnline &&
                location.ObservedAtUtc >= freshAfter, token);
        if (location is null) return [];
        var rides = await db.Rides.AsNoTracking()
            .Where(ride => ids.Contains(ride.Id) && ride.DriverId == null &&
                (ride.Status == RideStatus.Searching || ride.Status == RideStatus.Negotiating) &&
                ride.ServiceKindId == profile.ServiceKindId && ride.ServiceCatalogItemId == profile.ServiceCatalogItemId)
            .ToListAsync(token);
        return rides.Where(ride => RideDriverProximity.IsWithinRadius(ride, location.Latitude, location.Longitude, now))
            .Select(ride => ride.Id).ToHashSet();
    }
}
