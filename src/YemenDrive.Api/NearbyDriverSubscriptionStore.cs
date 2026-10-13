using System.Collections.Concurrent;

namespace YemenDrive.Api;

public sealed record NearbyDriverSubscriptionRequest(
    int? ServiceKindId,
    int? ServiceCatalogItemId,
    double PickupLatitude,
    double PickupLongitude,
    int RadiusMeters,
    IReadOnlyCollection<int> InitialDriverIds);

public sealed record NearbyDriverDelivery(string ConnectionId, bool IsAvailable, double DistanceMeters);

/// <summary>
/// Keeps only short-lived, connection-scoped nearby map subscriptions. Ride
/// and driver data remains in the database; the store only avoids broadcasting
/// every GPS sample to every signed-in customer.
/// </summary>
public sealed class NearbyDriverSubscriptionStore
{
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    public void Replace(string connectionId, NearbyDriverSubscriptionRequest request)
    {
        _subscriptions[connectionId] = new Subscription(
            request.ServiceKindId,
            request.ServiceCatalogItemId,
            request.PickupLatitude,
            request.PickupLongitude,
            Math.Clamp(request.RadiusMeters, 250, 25_000),
            request.InitialDriverIds.Where(id => id > 0).Take(50).ToHashSet());
    }

    public void Remove(string connectionId) => _subscriptions.TryRemove(connectionId, out _);

    public IReadOnlyList<NearbyDriverDelivery> Evaluate(
        int serviceKindId,
        int serviceCatalogItemId,
        int driverId,
        double latitude,
        double longitude,
        bool isAvailable)
    {
        var deliveries = new List<NearbyDriverDelivery>();
        foreach (var pair in _subscriptions)
        {
            var subscription = pair.Value;
            if (subscription.ServiceKindId is int kindId && kindId != serviceKindId) continue;
            if (subscription.ServiceCatalogItemId is int itemId && itemId != serviceCatalogItemId) continue;

            var distance = DistanceMeters(
                subscription.PickupLatitude,
                subscription.PickupLongitude,
                latitude,
                longitude);
            var currentlyAvailable = isAvailable && distance <= subscription.RadiusMeters;
            lock (subscription.KnownDriverIds)
            {
                var wasAvailable = subscription.KnownDriverIds.Contains(driverId);
                if (currentlyAvailable)
                {
                    subscription.KnownDriverIds.Add(driverId);
                    deliveries.Add(new NearbyDriverDelivery(pair.Key, true, distance));
                }
                else if (wasAvailable)
                {
                    subscription.KnownDriverIds.Remove(driverId);
                    deliveries.Add(new NearbyDriverDelivery(pair.Key, false, distance));
                }
            }
        }
        return deliveries;
    }

    private static double DistanceMeters(
        double firstLatitude,
        double firstLongitude,
        double secondLatitude,
        double secondLongitude)
    {
        const double earthRadiusMeters = 6_371_000;
        var first = firstLatitude * Math.PI / 180;
        var second = secondLatitude * Math.PI / 180;
        var latitudeDelta = (secondLatitude - firstLatitude) * Math.PI / 180;
        var longitudeDelta = (secondLongitude - firstLongitude) * Math.PI / 180;
        var value = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
            Math.Cos(first) * Math.Cos(second) *
            Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return 2 * earthRadiusMeters * Math.Atan2(Math.Sqrt(value), Math.Sqrt(1 - value));
    }

    private sealed record Subscription(
        int? ServiceKindId,
        int? ServiceCatalogItemId,
        double PickupLatitude,
        double PickupLongitude,
        int RadiusMeters,
        HashSet<int> KnownDriverIds);
}
