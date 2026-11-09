using YemenDrive.Api;

var checks = 0;
void Check(bool valid, string description)
{
    if (!valid) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

NearbyDriverSubscriptionRequest Watch(double lat, double lon, int? kind = 1, int? item = 2,
    int radius = 1000, int[]? known = null) => new(kind, item, lat, lon, radius, known ?? []);
var nearby = new NearbyDriverSubscriptionStore();
nearby.Replace("local", Watch(15.36, 44.19));
for (var i = 0; i < 10_000; i++) nearby.Replace("distant-" + i, Watch(35 + i % 10 * .01, -100));
var first = nearby.Evaluate(1, 2, 10, 15.36, 44.19, true);
Check(first.Count == 1 && first[0].ConnectionId == "local" && nearby.Metrics.EvaluatedSubscriptions == 1,
    "one local GPS update evaluates one candidate among 10,001 watches");
Check(nearby.Evaluate(1, 2, 10, 15.3601, 44.1901, true).Count == 1,
    "existing and spatial membership do not duplicate a live delivery");
var departure = nearby.Evaluate(1, 2, 10, -35, 100, true);
Check(departure.Count == 1 && departure[0].ConnectionId == "local" && !departure[0].IsAvailable,
    "a driver jumping beyond every nearby cell still removes its prior marker");
Check(nearby.Evaluate(1, 2, 10, -35, 100, false).Count == 0,
    "offline GPS does not scan unrelated watches or repeat a removal");
nearby.Replace("local", Watch(15.36, 44.19, known: [11]));
nearby.Replace("other-service", Watch(15.36, 44.19, 3, 4));
var changed = nearby.Evaluate(3, 4, 11, 15.36, 44.19, true);
Check(changed.Count == 2 && changed.Single(x => x.ConnectionId == "local").IsAvailable == false &&
    changed.Single(x => x.ConnectionId == "other-service").IsAvailable,
    "changing service removes previous viewers and updates only the matching service");
nearby.Replace("local", Watch(0, 0));
Check(nearby.Evaluate(1, 2, 11, 15.36, 44.19, false).All(x => x.ConnectionId != "local"),
    "replacing a viewport clears obsolete reverse membership");
nearby.Remove("local"); nearby.Remove("other-service");
for (var i = 0; i < 10_000; i++) nearby.Remove("distant-" + i);
Check(nearby.Metrics.Subscriptions == 0 && nearby.Metrics.SpatialBuckets == 0 &&
    nearby.Evaluate(3, 4, 11, 15.36, 44.19, false).Count == 0,
    "disconnect removes spatial and reverse references without accumulating empty cells");

var geographic = new NearbyDriverSubscriptionStore();
geographic.Replace("date-line", Watch(0, 179.999, radius: 1000));
geographic.Replace("north-pole", Watch(89.999, 0, radius: 1000));
geographic.Replace("south-pole", Watch(-89.999, 0, radius: 1000));
Check(geographic.Evaluate(1, 2, 20, 0, -179.999, true).Single().ConnectionId == "date-line",
    "a watch spans the date line without a full longitude scan");
Check(geographic.Evaluate(1, 2, 21, 89.999, 180, true).Single().ConnectionId == "north-pole" &&
    geographic.Evaluate(1, 2, 22, -89.999, 180, true).Single().ConnectionId == "south-pole",
    "both polar regions retain nearby watches across opposing longitudes");
geographic.Replace("any-service", Watch(0, 0, null, null));
geographic.Replace("any-item", Watch(0, 0, 1, null));
geographic.Replace("any-kind", Watch(0, 0, null, 2));
geographic.Replace("specific", Watch(0, 0));
Check(geographic.Evaluate(1, 2, 23, 0, 0, true).Count == 4,
    "nullable kind and catalog filters preserve all four valid subscription scopes");

// Independent great-circle destination generator exercises radius edges and
// cell boundaries at random locations, including near the poles/date line.
var random = new Random(20261106);
for (var i = 0; i < 500; i++)
{
    var lat = random.NextDouble() * 180 - 90;
    var lon = random.NextDouble() * 360 - 180;
    var radius = random.Next(250, 25_001);
    var store = new NearbyDriverSubscriptionStore();
    store.Replace("watch", Watch(lat, lon, radius: radius));
    var inside = Destination(lat, lon, radius - .1, random.NextDouble() * 2 * Math.PI);
    var outside = Destination(lat, lon, radius + .1, random.NextDouble() * 2 * Math.PI);
    if (store.Evaluate(1, 2, 1, inside.Lat, inside.Lon, true).SingleOrDefault()?.IsAvailable != true ||
        store.Evaluate(1, 2, 2, outside.Lat, outside.Lon, true).Count != 0)
        throw new InvalidOperationException("Spatial index lost exact radius membership at case " + i);
}
Check(true, "500 random spherical radius boundaries preserve exact inclusion/exclusion");
var concurrent = new NearbyDriverSubscriptionStore();
Parallel.For(0, 200, i =>
{
    concurrent.Replace(i.ToString(), Watch(15.36, 44.19, known: [i + 1]));
    concurrent.Evaluate(1, 2, i + 1, 15.36, 44.19, true);
    concurrent.Remove(i.ToString());
});
Check(concurrent.Metrics.Subscriptions == 0 && concurrent.Metrics.SpatialBuckets == 0,
    "concurrent GPS, replacement and disconnect keep indexes consistent");

var clock = new ManualClock();
var probe = new TestProbe();
var readiness = new ApiReadinessService(probe, clock);
Check(!await readiness.CheckAsync(default) && probe.Calls == 0, "unconfigured readiness fails without touching SQL");
probe.IsConfigured = true;
var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
probe.Handler = _ => blocked.Task;
var clients = Enumerable.Range(0, 100).Select(_ => readiness.CheckAsync(default)).ToArray();
await Task.Delay(30);
blocked.SetResult(true);
Check((await Task.WhenAll(clients)).All(x => x) && probe.Calls == 1,
    "100 concurrent readiness clients share one database probe");
Check(await readiness.CheckAsync(default) && probe.Calls == 1, "a fresh readiness result reuses the one-second server cache");
clock.Advance(); probe.Handler = _ => throw new IOException("private database failure detail");
Check(!await readiness.CheckAsync(default) && probe.Calls == 2, "database outage returns unavailable without exception details");
clock.Advance(); probe.Handler = _ => Task.FromResult(true);
Check(await readiness.CheckAsync(default) && probe.Calls == 3, "readiness recovers automatically after its short cache expires");

clock.Advance();
var uncancellable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
probe.Handler = _ => uncancellable.Task;
Check(!await readiness.CheckAsync(default), "a database that ignores cancellation cannot hold the readiness response indefinitely");
var again = readiness.CheckAsync(default);
await Task.Delay(30);
Check(probe.Calls == 4, "a timed-out database attempt stays shared rather than spawning retry connections");
uncancellable.SetResult(true);
Check(!await again, "completion after the probe deadline cannot advertise ready");
clock.Advance();
var shared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
probe.Handler = _ => shared.Task;
using var cancellation = new CancellationTokenSource();
var abandoned = readiness.CheckAsync(cancellation.Token);
var waiting = readiness.CheckAsync(default);
cancellation.Cancel();
try { await abandoned; throw new InvalidOperationException("Caller cancellation ignored"); }
catch (OperationCanceledException) { }
shared.SetResult(true);
Check(await waiting && probe.Calls == 5, "one disconnected HTTP caller cannot cancel another caller's shared probe");
await ReadinessHttpScenarios.RunAsync(Check);
Console.WriteLine($"PASS: {checks} realtime responsiveness assertions.");

static (double Lat, double Lon) Destination(double latitude, double longitude, double meters, double bearing)
{
    var lat = latitude * Math.PI / 180; var lon = longitude * Math.PI / 180; var arc = meters / 6_371_000;
    var nextLat = Math.Asin(Math.Sin(lat) * Math.Cos(arc) + Math.Cos(lat) * Math.Sin(arc) * Math.Cos(bearing));
    var nextLon = lon + Math.Atan2(Math.Sin(bearing) * Math.Sin(arc) * Math.Cos(lat), Math.Cos(arc) - Math.Sin(lat) * Math.Sin(nextLat));
    return (nextLat * 180 / Math.PI, (nextLon * 180 / Math.PI + 540) % 360 - 180);
}

sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance() => now += TimeSpan.FromSeconds(2);
}
sealed class TestProbe : IApiReadinessProbe
{
    public bool IsConfigured { get; set; }
    public int Calls;
    public Func<CancellationToken, Task<bool>> Handler = _ => Task.FromResult(true);
    public Task<bool> CheckAsync(CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        return Handler(token);
    }
}
