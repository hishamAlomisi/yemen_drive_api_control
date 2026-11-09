using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api;
using YemenDrive.Api.Tracking;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using Ride = YemenDrive.Database.Entities.Ride;

var name = "YemenDrive_RouteSampleTests_" + Guid.NewGuid().ToString("N");
var options = new DbContextOptionsBuilder<YemenDriveDbContext>()
    .UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={name};Integrated Security=True;TrustServerCertificate=True").Options;
var settings = new TrackingUpgradeSettings { DurableSamplesEnabled = true };
var realtime = DispatchProxy.Create<IRideRealtimePublisher, RecordingProxy>();
var recorder = (RecordingProxy)(object)realtime;
var transport = new TestHubContext();
RouteSampleBatchService Service(YemenDriveDbContext db, TrackingUpgradeSettings? use = null) => new(db, use ?? settings,
    new(db, realtime, new(), transport, new TestLifetime(), NullLogger<DriverLocationIngestionService>.Instance),
    NullLogger<RouteSampleBatchService>.Instance);
async Task<RouteSampleBatchResult> Send(int driverId, RouteSampleBatchRequest batch)
{
    await using var db = new YemenDriveDbContext(options);
    return await Service(db).SaveAsync(driverId, batch, default);
}
try
{
    await VisualQueueScenarios.RunAsync();
    await using var seed = new YemenDriveDbContext(options);
    await seed.Database.MigrateAsync();
    Check(!seed.Database.HasPendingModelChanges(), "additive route migration matches runtime model");
    var now = DateTime.UtcNow;
    var customer = new User { PhoneNumber = "route-customer", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "route-driver", Role = UserRole.Driver };
    var other = new User { PhoneNumber = "route-other", Role = UserRole.Driver };
    var kind = new ServiceKind { Code = "route-kind", NameAr = "test" };
    var catalog = new RideServiceCatalogItem { Code = "route-car", NameAr = "test", ServiceKind = kind };
    var ride = new Ride { Customer = customer, Driver = driver, ServiceKind = kind, ServiceCatalogItem = catalog,
        Status = RideStatus.InProgress, StartedAtUtc = now.AddMinutes(-2), CreatedAtUtc = now.AddMinutes(-5),
        RouteSegmentStartedAtUtc = now.AddSeconds(-90), RouteSegmentLatitude = 15.36, RouteSegmentLongitude = 44.19 };
    seed.Rides.Add(ride);
    seed.DriverProfiles.Add(new DriverProfile { User = driver, ServiceKind = kind, ServiceCatalogItem = catalog, IsApproved = true });
    seed.DriverProfiles.Add(new DriverProfile { User = other, ServiceKind = kind, ServiceCatalogItem = catalog, IsApproved = true });
    await seed.SaveChangesAsync();
    var session = Guid.NewGuid();
    RouteSampleRequest Sample(long sequence, int ago, double latitude = 15.36) => new(Guid.NewGuid(), sequence,
        latitude, 44.19, 0, 10, 5, now.AddSeconds(-ago));
    var first = Sample(1, 70, 15.3601);
    var middle = Sample(2, 60, 15.3602);
    var last = Sample(3, 50, 15.3603);
    RouteSampleBatchRequest Batch(params RouteSampleRequest[] samples) => new(ride.Id, session, samples);

    var disabled = await Service(seed, new()).SaveAsync(driver.Id, Batch(first), default);
    Check(!disabled.Enabled && disabled.AcceptedSampleIds.Length == 0 && !await seed.DriverRouteSamples.AnyAsync(), "disabled rollout does not claim receipts or change route data");
    var sparse = await Send(driver.Id, Batch(first, last));
    Check(sparse.AcceptedSampleIds.Length == 2 && sparse.LastContiguousSequence == 1 && !sparse.LivePublished,
        "historical samples commit without viewers or a live broadcast, while a sequence gap stays visible");
    Check(!await seed.DriverLiveLocations.AnyAsync(), "history cannot make a driver online or move the live position");
    var boundary = new DriverLiveLocation { DriverId = driver.Id, Latitude = last.Latitude, Longitude = last.Longitude, ObservedAtUtc = last.ObservedAtUtc };
    await Reject<ServiceException>(() => RideRouteChangePolicy.MeasureAsync(seed, ride, boundary, default), "known missing sequence blocks route pricing");
    var filled = await Send(driver.Id, Batch(middle));
    Check(filled.LastContiguousSequence == 3, "out-of-order backfill repairs the contiguous receipt boundary");
    var unsavedBoundary = new DriverLiveLocation { DriverId = driver.Id, Latitude = last.Latitude + .0001,
        Longitude = last.Longitude, ObservedAtUtc = last.ObservedAtUtc.AddSeconds(1) };
    await Reject<ServiceException>(() => RideRouteChangePolicy.MeasureAsync(seed, ride, unsavedBoundary, default),
        "a durable trip cannot price a live anchor ahead of its archived observations");
    var beforeRetry = await RideRouteChangePolicy.MeasureAsync(seed, ride, boundary, default);
    await Send(driver.Id, Batch(first, middle, last));
    var afterRetry = await RideRouteChangePolicy.MeasureAsync(seed, ride, boundary, default);
    Check(await seed.DriverRouteSamples.CountAsync() == 3 && await seed.LocationUpdates.CountAsync() == 3 &&
        beforeRetry == afterRetry && beforeRetry.Meters > 30, "lost reply and process/context restart retries preserve route count and measured fare inputs");
    await Reject<HubException>(() => Send(driver.Id, Batch(middle with { Latitude = 16 })), "sample identity cannot be reused with a different payload");
    await Reject<HubException>(() => Send(other.Id, Batch(middle)), "a different authenticated driver cannot append to this trip");
    await Reject<HubException>(() => Send(driver.Id, Batch(Sample(4, 40), Sample(5, 30) with { Accuracy = 200 })), "one invalid sample rejects the complete batch before acknowledgement");
    Check(await seed.DriverRouteSamples.CountAsync() == 3, "rejected batch leaves no partial receipts");
    await Reject<HubException>(() => Send(driver.Id, new(ride.Id, Guid.NewGuid(), [Sample(1, 45, 16)])), "a new device session cannot bypass plausibility against the existing route archive");

    var fresh = Sample(4, 2, 15.3604) with { ObservedAtUtc = DateTime.UtcNow.AddSeconds(-2) };
    var recent = await Send(driver.Id, Batch(fresh));
    Check(recent.LivePublished && recorder.Locations.Count == 1 && await seed.LocationUpdates.CountAsync() == 4,
        "the newest recent sample updates live delivery after commit without duplicating the route archive");
    var liveBefore = await seed.DriverLiveLocations.AsNoTracking().SingleAsync();
    await Send(driver.Id, Batch(first, middle));
    var liveAfter = await seed.DriverLiveLocations.AsNoTracking().SingleAsync();
    Check(liveBefore.ObservedAtUtc == liveAfter.ObservedAtUtc && liveBefore.Latitude == liveAfter.Latitude,
        "historical retry after a newer fix cannot rewind live state");

    var concurrentSample = Sample(5, 1, 15.3605) with { ObservedAtUtc = DateTime.UtcNow.AddSeconds(-1) };
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send(driver.Id, Batch(concurrentSample))));
    Check(await seed.DriverRouteSamples.CountAsync() == 5 && await seed.LocationUpdates.CountAsync() == 5,
        "concurrent duplicate Hub/HTTP submissions commit one receipt and one route point");

    var failingOptions = new DbContextOptionsBuilder<YemenDriveDbContext>(options).AddInterceptors(new FailReceipt()).Options;
    await using (var failing = new YemenDriveDbContext(failingOptions))
        await RejectReceiptFailure(() => Service(failing).SaveAsync(driver.Id, Batch(Sample(6, 0, 15.3606) with { ObservedAtUtc = DateTime.UtcNow }), default));
    Check(await seed.DriverRouteSamples.CountAsync() == 5 && await seed.LocationUpdates.CountAsync() == 5,
        "receipt persistence failure rolls back route data and does not acknowledge it");

    var mixedSample = Sample(6, 0, 15.3606) with { ObservedAtUtc = DateTime.UtcNow };
    async Task LegacySend()
    {
        await using var db = new YemenDriveDbContext(options);
        var ingestion = new DriverLocationIngestionService(db, realtime, new(), transport, new TestLifetime(), NullLogger<DriverLocationIngestionService>.Instance);
        await ingestion.SaveAsync(driver.Id, new(ride.Id, mixedSample.Latitude, mixedSample.Longitude,
            mixedSample.Bearing, mixedSample.Speed, mixedSample.Accuracy, mixedSample.ObservedAtUtc));
    }
    await Task.WhenAll(LegacySend(), Send(driver.Id, Batch(mixedSample)));
    Check(await seed.DriverRouteSamples.CountAsync() == 6 && await seed.LocationUpdates.CountAsync() == 6,
        "legacy fallback concurrent with batch retry preserves one route point per observation");

    var watches = new RideMapWatchStore();
    var context = new DefaultHttpContext(); context.Items[AuthSessionAuthenticationMiddleware.UserIdItem] = customer.Id;
    var hub = new TrackingHub(seed, new(seed), new(), NullLogger<TrackingHub>.Instance,
        new(seed, realtime, new(), transport, new TestLifetime(), NullLogger<DriverLocationIngestionService>.Instance),
        mapWatches: watches) { Context = new TestCaller(context), Clients = new TestCallerClients(transport.Proxy) };
    var watched = await hub.WatchRideMap(ride.Id);
    Check(watched.Enabled && watched.Location?.ObservedAtUtc == mixedSample.ObservedAtUtc &&
        watched.Location.ObservedAtUtc.Kind == DateTimeKind.Utc && watches.HasWatchers(ride.Id),
        "opening an authorized map returns the latest saved observation immediately with its original capture time");
    await hub.SetForeground(false);
    Check(!watches.HasWatchers(ride.Id), "backgrounding clears map interest independently of business subscriptions");
    context.Items[AuthSessionAuthenticationMiddleware.UserIdItem] = other.Id;
    await Reject<HubException>(() => hub.WatchRideMap(ride.Id), "map snapshot requires participant authorization");
    context.Items[AuthSessionAuthenticationMiddleware.UserIdItem] = customer.Id;
    customer.IsActive = false; await seed.SaveChangesAsync();
    await Reject<HubException>(() => hub.WatchRideMap(ride.Id), "a disabled participant cannot renew map access on an existing connection");
    customer.IsActive = true; await seed.SaveChangesAsync();
    watches.Watch("expired", ride.Id, DateTime.UtcNow.AddSeconds(-1));
    Check(!watches.HasWatchers(ride.Id), "abandoned interest expires without requiring a client cleanup message");

    var visualQueue = new DriverLocationVisualQueue(new() { BufferedVisualDeliveryEnabled = true, VisualQueueCapacity = 1 });
    var publishedBeforeQueue = recorder.Locations.Count;
    var queuedAt = DateTime.UtcNow.AddSeconds(1);
    var queuedFirst = Sample(7, 0, 15.3607) with { ObservedAtUtc = queuedAt };
    var queuedLast = Sample(8, 0, 15.3608) with { ObservedAtUtc = queuedAt.AddSeconds(1) };
    foreach (var sample in new[] { queuedFirst, queuedLast })
    {
        await using var db = new YemenDriveDbContext(options);
        var ingestion = new DriverLocationIngestionService(db, realtime, new(), transport, new TestLifetime(),
            NullLogger<DriverLocationIngestionService>.Instance, visualQueue);
        await new RouteSampleBatchService(db, settings, ingestion, NullLogger<RouteSampleBatchService>.Instance)
            .SaveAsync(driver.Id, Batch(sample), default);
    }
    Check(await seed.DriverRouteSamples.CountAsync() == 8 && await seed.LocationUpdates.CountAsync() == 8 &&
        visualQueue.Metrics.Pending == 1 && visualQueue.Metrics.Coalesced == 1 && recorder.Locations.Count == publishedBeforeQueue,
        "coalescing visual delivery preserves every durable receipt and route point without waiting on transport");

    var serviceEnd = DateTime.UtcNow.AddSeconds(3);
    ride.ServiceEndedAtUtc = serviceEnd; ride.CompletedAtUtc = serviceEnd; ride.Status = RideStatus.Completed;
    await seed.SaveChangesAsync();
    await Reject<HubException>(() => hub.WatchRideMap(ride.Id), "a completed trip cannot expose the driver's later live position");
    await Reject<HubException>(() => Send(driver.Id, Batch(Sample(9, -2, 15.3609) with { ObservedAtUtc = serviceEnd.AddMilliseconds(1) })), "post-service observations cannot be backfilled into a completed trip");
    ride.ServiceEndedAtUtc = null; await seed.SaveChangesAsync();
    await Reject<HubException>(() => hub.WatchRideMap(ride.Id), "terminal ride status blocks watching even if an old record lacks a service-end timestamp");
    await VisualTransportScenarios.RunAsync(seed, other, customer, kind, catalog, realtime, recorder, transport);
    await DirectVisualCancellationScenarios.RunAsync(options, customer.Id, kind.Id, catalog.Id, realtime);
    Console.WriteLine("PASS: durable route batches, restart/retry, ordered history, route pricing guard, map freshness and permissions on isolated SQL.");
}
finally
{
    if (!name.StartsWith("YemenDrive_RouteSampleTests_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup.");
    await using var cleanup = new YemenDriveDbContext(options);
    await cleanup.Database.EnsureDeletedAsync();
}

static void Check(bool valid, string description) { if (!valid) throw new InvalidOperationException(description); Console.WriteLine("PASS: " + description); }
static async Task Reject<T>(Func<Task> action, string description) where T : Exception
{
    try { await action(); } catch (T) { Check(true, description); return; }
    throw new InvalidOperationException(description + ": was not rejected");
}
static async Task RejectReceiptFailure(Func<Task> action)
{
    try { await action(); }
    catch (DbUpdateException error) when (error.InnerException is IOException { Message: "injected durable sample failure" })
    {
        Check(true, "injected persistence failure reaches the receipt transaction");
        return;
    }
    throw new InvalidOperationException("Receipt fault injection was not reached.");
}
public class RecordingProxy : DispatchProxy
{
    public List<DriverLocationRealtimeEvent> Locations { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (args?[0] is DriverLocationRealtimeEvent point) lock (Locations) Locations.Add(point);
        return Task.CompletedTask;
    }
}
sealed class TestLifetime(CancellationToken stopping = default) : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => default;
    public CancellationToken ApplicationStopping => stopping;
    public CancellationToken ApplicationStopped => default;
    public void StopApplication() { }
}
sealed class FailReceipt : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        if (command.CommandText.Contains("INSERT INTO [DriverRouteSamples]")) throw new IOException("injected durable sample failure");
        return ValueTask.FromResult(result);
    }
}
sealed class TestProxy : IClientProxy
{
    public System.Collections.Concurrent.ConcurrentQueue<(string Method, object?[] Arguments)> Messages { get; } = new();
    public Func<CancellationToken, Task>? OnSend { get; set; }
    public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Messages.Enqueue((method, args));
        return OnSend?.Invoke(ct) ?? Task.CompletedTask;
    }
}
sealed class TestHubContext : IHubContext<TrackingHub>
{
    public TestProxy Proxy { get; } = new();
    public IHubClients Clients => new TestCallerClients(Proxy);
    public IGroupManager Groups => throw new NotSupportedException();
}
sealed class TestCallerClients(TestProxy proxy) : IHubCallerClients, IHubClients
{
    public IClientProxy All => proxy;
    public IClientProxy Caller => proxy;
    public IClientProxy Others => proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> ids) => proxy;
    public IClientProxy Client(string id) => proxy;
    public IClientProxy Clients(IReadOnlyList<string> ids) => proxy;
    public IClientProxy Group(string id) => proxy;
    public IClientProxy GroupExcept(string id, IReadOnlyList<string> ids) => proxy;
    public IClientProxy Groups(IReadOnlyList<string> ids) => proxy;
    public IClientProxy OthersInGroup(string id) => proxy;
    public IClientProxy User(string id) => proxy;
    public IClientProxy Users(IReadOnlyList<string> ids) => proxy;
}
sealed class HttpFeature(HttpContext context) : IHttpContextFeature { public HttpContext? HttpContext { get; set; } = context; }
sealed class TestCaller : HubCallerContext
{
    public TestCaller(HttpContext context) { Features.Set<IHttpContextFeature>(new HttpFeature(context)); }
    public override string ConnectionId => "route-test-caller";
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User => null;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => default;
    public override void Abort() { }
}
