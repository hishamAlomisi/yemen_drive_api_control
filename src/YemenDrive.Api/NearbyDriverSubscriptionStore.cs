namespace YemenDrive.Api;

public sealed record NearbyDriverSubscriptionRequest(
    int? ServiceKindId,
    int? ServiceCatalogItemId,
    double PickupLatitude,
    double PickupLongitude,
    int RadiusMeters,
    IReadOnlyCollection<int> InitialDriverIds);

public sealed record NearbyDriverDelivery(string ConnectionId, bool IsAvailable, double DistanceMeters);
public sealed record NearbyDriverSubscriptionMetrics(int Subscriptions, int SpatialBuckets,
    long Evaluations, long EvaluatedSubscriptions, long Deliveries);

/// <summary>Connection-scoped nearby watches indexed by service and position.
/// Reverse membership also finds previous viewers when a driver leaves their area.</summary>
public sealed class NearbyDriverSubscriptionStore
{
    private const double EarthRadiusMeters = 6_371_000;
    private const int MaximumRadiusMeters = 25_000;
    // A chord is never longer than its surface arc. In a unit-sphere grid of
    // this size, every watch within the maximum radius is in one of 27 cells.
    // Unlike a longitude grid, this remains bounded at the poles/date line.
    private const double CellSize = MaximumRadiusMeters / EarthRadiusMeters;
    private readonly object gate = new();
    private readonly Dictionary<string, Subscription> subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<Bucket, HashSet<string>> buckets = new();
    private readonly Dictionary<int, HashSet<string>> driverViewers = new();
    private long evaluations, evaluatedSubscriptions, delivered;

    public NearbyDriverSubscriptionMetrics Metrics
    {
        get { lock (gate) return new(subscriptions.Count, buckets.Count, evaluations, evaluatedSubscriptions, delivered); }
    }

    public void Replace(string connectionId, NearbyDriverSubscriptionRequest request)
    {
        ValidateCoordinates(request.PickupLatitude, request.PickupLongitude);
        var cell = CellAt(request.PickupLatitude, request.PickupLongitude);
        var subscription = new Subscription(request.ServiceKindId, request.ServiceCatalogItemId,
            request.PickupLatitude, request.PickupLongitude, Math.Clamp(request.RadiusMeters, 250, MaximumRadiusMeters),
            request.InitialDriverIds.Where(id => id > 0).Take(50).ToHashSet(),
            new(request.ServiceKindId, request.ServiceCatalogItemId, cell.X, cell.Y, cell.Z));
        lock (gate)
        {
            RemoveCore(connectionId);
            subscriptions.Add(connectionId, subscription);
            Add(buckets, subscription.Bucket, connectionId);
            foreach (var driverId in subscription.KnownDriverIds) Add(driverViewers, driverId, connectionId);
        }
    }

    public void Remove(string connectionId)
    {
        lock (gate) RemoveCore(connectionId);
    }

    public IReadOnlyList<NearbyDriverDelivery> Evaluate(int serviceKindId, int serviceCatalogItemId,
        int driverId, double latitude, double longitude, bool isAvailable)
    {
        ValidateCoordinates(latitude, longitude);
        var cell = CellAt(latitude, longitude);
        lock (gate)
        {
            // Known viewers are included even if a jump or service change puts
            // them outside all current buckets; they still need a removal.
            var candidates = driverViewers.TryGetValue(driverId, out var previous)
                ? new HashSet<string>(previous, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            if (isAvailable)
            {
                for (var x = cell.X - 1; x <= cell.X + 1; x++)
                for (var y = cell.Y - 1; y <= cell.Y + 1; y++)
                for (var z = cell.Z - 1; z <= cell.Z + 1; z++)
                {
                    Include(new(serviceKindId, serviceCatalogItemId, x, y, z));
                    Include(new(serviceKindId, null, x, y, z));
                    Include(new(null, serviceCatalogItemId, x, y, z));
                    Include(new(null, null, x, y, z));
                }
            }
            evaluations++;
            evaluatedSubscriptions += candidates.Count;
            var deliveries = new List<NearbyDriverDelivery>(candidates.Count);
            foreach (var connectionId in candidates)
            {
                var subscription = subscriptions[connectionId];
                var distance = DistanceMeters(subscription.PickupLatitude, subscription.PickupLongitude, latitude, longitude);
                var currentlyAvailable = isAvailable && distance <= subscription.RadiusMeters &&
                    (subscription.ServiceKindId is null || subscription.ServiceKindId == serviceKindId) &&
                    (subscription.ServiceCatalogItemId is null || subscription.ServiceCatalogItemId == serviceCatalogItemId);
                if (currentlyAvailable)
                {
                    if (subscription.KnownDriverIds.Add(driverId)) Add(driverViewers, driverId, connectionId);
                    deliveries.Add(new(connectionId, true, distance));
                }
                else if (subscription.KnownDriverIds.Remove(driverId))
                {
                    Remove(driverViewers, driverId, connectionId);
                    deliveries.Add(new(connectionId, false, distance));
                }
            }
            delivered += deliveries.Count;
            return deliveries;

            void Include(Bucket bucket)
            {
                if (buckets.TryGetValue(bucket, out var viewers)) candidates.UnionWith(viewers);
            }
        }
    }

    private void RemoveCore(string connectionId)
    {
        if (!subscriptions.Remove(connectionId, out var previous)) return;
        Remove(buckets, previous.Bucket, connectionId);
        foreach (var driverId in previous.KnownDriverIds) Remove(driverViewers, driverId, connectionId);
    }

    private static void Add<TKey>(Dictionary<TKey, HashSet<string>> index, TKey key, string connectionId) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var entries)) index.Add(key, entries = new(StringComparer.Ordinal));
        entries.Add(connectionId);
    }

    private static void Remove<TKey>(Dictionary<TKey, HashSet<string>> index, TKey key, string connectionId) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var entries)) return;
        entries.Remove(connectionId);
        if (entries.Count == 0) index.Remove(key);
    }

    private static (int X, int Y, int Z) CellAt(double latitude, double longitude)
    {
        var lat = latitude * Math.PI / 180;
        var lon = longitude * Math.PI / 180;
        var cosLat = Math.Cos(lat);
        return ((int)Math.Floor(cosLat * Math.Cos(lon) / CellSize),
            (int)Math.Floor(cosLat * Math.Sin(lon) / CellSize),
            (int)Math.Floor(Math.Sin(lat) / CellSize));
    }

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 ||
            !double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Coordinates must be finite and on Earth.");
    }

    private static double DistanceMeters(double firstLatitude, double firstLongitude, double secondLatitude, double secondLongitude)
    {
        var first = firstLatitude * Math.PI / 180;
        var second = secondLatitude * Math.PI / 180;
        var latitudeDelta = (secondLatitude - firstLatitude) * Math.PI / 180;
        var longitudeDelta = (secondLongitude - firstLongitude) * Math.PI / 180;
        var value = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
            Math.Cos(first) * Math.Cos(second) * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        value = Math.Clamp(value, 0, 1);
        return 2 * EarthRadiusMeters * Math.Atan2(Math.Sqrt(value), Math.Sqrt(1 - value));
    }

    private readonly record struct Bucket(int? ServiceKindId, int? ServiceCatalogItemId, int X, int Y, int Z);
    private sealed record Subscription(int? ServiceKindId, int? ServiceCatalogItemId, double PickupLatitude,
        double PickupLongitude, int RadiusMeters, HashSet<int> KnownDriverIds, Bucket Bucket);
}
