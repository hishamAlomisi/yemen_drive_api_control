using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api;
using YemenDrive.Api.Tracking;
using YemenDrive.Application.Administration;
using YemenDrive.Application.Drivers;
using YemenDrive.Application.Maps;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

public static class AdminLocationScenarios
{
    public static async Task RunAsync(DbContextOptions<YemenDriveDbContext> options, DatabaseConfigurationStore config, Action<bool, string> check)
    {
        var route = new[] { new RoutePoint(15.36, 44.19), new RoutePoint(15.361, 44.19), new RoutePoint(15.361, 44.192) };
        var step = RouteSimulationGeometry.Advance(route, route[0]);
        check(Math.Abs(RouteSimulationGeometry.Distance(route[0], step.Point) - 1) < .001, "simulation advances exactly one metre along route");
        var next = RouteSimulationGeometry.Advance(route, step.Point);
        check(Math.Abs(RouteSimulationGeometry.Distance(step.Point, next.Point) - 1) < .001, "successive clicks accumulate without polyline rounding reset");
        var corner = new RoutePoint(15.361 - .4 / 111194.9266, 44.19);
        var turned = RouteSimulationGeometry.Advance(route, corner);
        check(turned.Point.Longitude > 44.19 && Math.Abs(turned.Point.Latitude - 15.361) < 1e-9 && Math.Abs(turned.DistanceMeters - 1) < .001, "one-metre movement traverses route corner");
        var end = RouteSimulationGeometry.Advance(route, new(15.361, 44.192 - .2 / 107223));
        check(end.Point == route[^1] && end.DistanceMeters < 1, "last step stops at route endpoint without overshoot");
        Expect(() => RouteSimulationGeometry.Advance(route, new(16, 44)), "simulation_off_route");
        Expect(() => RouteSimulationGeometry.Advance([], route[0]), "simulation_route_missing");
        check(RouteSimulationGeometry.Decode("?").Count == 0 && RouteSimulationGeometry.Decode("~").Count == 0, "malformed polyline rejected safely");
        var encoded = Encode(route);
        check(RouteSimulationGeometry.Decode(encoded).SequenceEqual(route), "encoded route retains expected points");

        await using var db = new YemenDriveDbContext(options);
        var driver = new User { PhoneNumber = "movement-admin-driver", Role = UserRole.Driver };
        var rider = new User { PhoneNumber = "movement-admin-rider", Role = UserRole.Customer };
        var admin = new User { PhoneNumber = "movement-admin-system", Role = UserRole.Admin, IsSystemAdministrator = true };
        var limited = new User { PhoneNumber = "movement-admin-limited", Role = UserRole.Admin };
        var kind = new ServiceKind { Code = "movement-admin-kind", NameAr = "اختبار الحركة" };
        var catalog = new RideServiceCatalogItem { Code = "movement-admin-car", NameAr = "اختبار", ServiceKind = kind };
        db.AddRange(driver, rider, admin, limited, kind, catalog); await db.SaveChangesAsync();
        db.DriverProfiles.Add(new() { UserId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = catalog.Id, IsApproved = true, IsAvailable = true, AcceptsNewRequests = true });
        var ride = new Ride { CustomerId = rider.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = catalog.Id,
            Status = RideStatus.InProgress, StartedAtUtc = DateTime.UtcNow.AddMinutes(-1), RoutePolyline = encoded,
            PickupLatitude = route[0].Latitude, PickupLongitude = route[0].Longitude, DestinationLatitude = route[^1].Latitude, DestinationLongitude = route[^1].Longitude };
        db.Rides.Add(ride); await db.SaveChangesAsync();
        var hub = new AdminRecordingHub<TrackingHub>();
        var publisher = new SignalRRideRealtimePublisher(hub, new AdminRecordingHub<SharedRideHub>(), db, NullLogger<SignalRRideRealtimePublisher>.Instance);
        var nearby = new NearbyDriverSubscriptionStore();
        var ingestion = new DriverLocationIngestionService(db, publisher, nearby, hub, new Lifetime(), NullLogger<DriverLocationIngestionService>.Instance);
        var provider = new RouteHandler(encoded);
        using var http = new HttpClient(provider);
        var maps = new GoogleMapsGateway(http, new GoogleMapsOptions { RoutesApiKey = "isolated-test-key" }, NullLogger<GoogleMapsGateway>.Instance);
        var implementation = new DriverLocationAdministrator(db, ingestion, maps);
        var service = new DriverLocationAdmin(db, config, new AdminActor(admin.Id), implementation);
        var save = await Run(service, "save", new { driverId = driver.Id, latitude = route[0].Latitude, longitude = route[0].Longitude });
        check(save.Success, "admin can save location with no previous GPS");
        check(hub.Events.Any(x => x.Name == "driverLocationUpdated"), "manual location uses normal live ride broadcaster");
        var archive = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == ride.Id).ToListAsync();
        check(archive.Count == 1 && archive[0].Source == "admin_manual", "manual coordinate is archived with explicit test source");
        var moved = await Run(service, "move", new { driverId = driver.Id });
        check(moved.Success, "admin move succeeds during active trip");
        var movedAgain = await Run(service, "move", new { driverId = driver.Id });
        check(movedAgain.Success && await db.LocationUpdates.CountAsync(x => x.RideId == ride.Id && x.Source == "admin_simulation") == 2, "rapid separate clicks bypass GPS throttle and archive both steps");
        var actual = await db.DriverLiveLocations.AsNoTracking().SingleAsync(x => x.DriverId == driver.Id);
        check(Math.Abs(RouteSimulationGeometry.Distance(route[0], new(actual.Latitude, actual.Longitude)) - 2) < .01, "SQL live coordinate is two metres forward after two clicks");
        var invalid = await Run(service, "save", new { driverId = driver.Id, latitude = 91, longitude = 44 });
        check(!invalid.Success && invalid.Code == "invalid_location", "invalid coordinates cannot be persisted");
        foreach (var actor in new[] { driver.Id, rider.Id, limited.Id })
        {
            var blocked = await Run(new DriverLocationAdmin(db, config, new AdminActor(actor), implementation), "save", new { driverId = driver.Id, latitude = 15, longitude = 44 });
            check(!blocked.Success && blocked.Code == "administrator_permission_required", "non-admin/unprivileged admin cannot set driver position");
        }
        try { await new AdministratorAccess(db, new AdminActor(rider.Id)).AuthorizeAsync(new ApiRequest("DriverLocationAdminModel", "get", JsonSerializer.SerializeToElement(new { driverId = driver.Id })), default); throw new Exception("Expected role restriction"); }
        catch (ServiceException e) { check(e.Code == "administrator_required", "public dispatcher rejects admin location model for rider"); }

        ride.Status = RideStatus.DriverEnRoute; ride.StartedAtUtc = null; await db.SaveChangesAsync();
        check((await Run(service, "save", new { driverId = driver.Id, latitude = route[0].Latitude, longitude = route[0].Longitude })).Success, "admin can reposition before pickup");
        check((await Run(service, "move", new { driverId = driver.Id })).Success && provider.Calls > 0, "pickup approach uses provider road route");
        check(await db.LocationUpdates.CountAsync(x => x.RideId == ride.Id) == 5, "pickup approach samples are archived before ride starts");
        await ingestion.SaveAsync(driver.Id, new(ride.Id, route[0].Latitude + .00002, route[0].Longitude, 0, 1, 5, DateTime.UtcNow.AddSeconds(2)));
        check(await db.LocationUpdates.AnyAsync(x => x.RideId == ride.Id && x.Source == "gps"), "ordinary driver GPS also archives pickup approach with GPS source");
        var batch = new RouteSampleBatchService(db, new TrackingUpgradeSettings { DurableSamplesEnabled = true }, ingestion, NullLogger<RouteSampleBatchService>.Instance);
        var batchPoint = new RouteSampleRequest(Guid.NewGuid(), 1, route[0].Latitude + .00003, route[0].Longitude, 0, 1, 5, DateTime.UtcNow.AddSeconds(3));
        var receipt = await batch.SaveAsync(driver.Id, new(ride.Id, Guid.NewGuid(), [batchPoint]), default);
        check(receipt.AcceptedSampleIds.Length == 1 && await db.LocationUpdates.AnyAsync(x => x.RideId == ride.Id && x.ObservedAtUtc == batchPoint.ObservedAtUtc && x.Source == "gps"),
            "durable batch archives pickup approach before StartedAtUtc for report and live recovery");
        ride.Status = RideStatus.DriverAssigned; await db.SaveChangesAsync();
        var beforeRouteRace = await db.LocationUpdates.CountAsync(x => x.RideId == ride.Id);
        provider.BeforeResponse = async () =>
        {
            await using var changed = new YemenDriveDbContext(options);
            var updatedRide = await changed.Rides.SingleAsync(x => x.Id == ride.Id);
            updatedRide.PickupLatitude += .001; await changed.SaveChangesAsync();
        };
        var routeRace = await Run(service, "move", new { driverId = driver.Id });
        check(!routeRace.Success && routeRace.Code == "invalid_location" && await db.LocationUpdates.CountAsync(x => x.RideId == ride.Id) == beforeRouteRace,
            "trip target change during route lookup rejects obsolete simulation without saving");
        provider.BeforeResponse = null; await db.Entry(ride).ReloadAsync();
        ride.Status = RideStatus.Completed; ride.ServiceEndedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync();
        var completed = await Run(service, "move", new { driverId = driver.Id });
        check(!completed.Success && completed.Code == "simulation_ride_required", "completed ride cannot be simulated");
        nearby.Replace("nearby-viewer", new(kind.Id, catalog.Id, route[0].Latitude, route[0].Longitude, 3000, []));
        check((await Run(service, "save", new { driverId = driver.Id, latitude = route[0].Latitude, longitude = route[0].Longitude })).Success, "idle driver can be placed manually");
        check(hub.Events.Any(x => x.Name == "nearbyDriverLocationUpdated" && x.Targets.Contains("nearby-viewer")), "manual idle location reaches nearby map subscribers");

        void Expect(Action action, string code) { try { action(); throw new Exception("Expected error " + code); } catch (ServiceException e) { check(e.Code == code, "geometry rejects " + code); } }
    }
    private static Task<ApiResult> Run(DriverLocationAdmin service, string operation, object model) => service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), default);
    private static string Encode(IReadOnlyList<RoutePoint> points)
    {
        var text = new System.Text.StringBuilder(); long lat = 0, lng = 0;
        foreach (var p in points) { var nextLat = (long)Math.Round(p.Latitude * 1e5); var nextLng = (long)Math.Round(p.Longitude * 1e5); Add(nextLat - lat); Add(nextLng - lng); lat = nextLat; lng = nextLng; }
        return text.ToString();
        void Add(long number) { var n = number < 0 ? ~(number << 1) : number << 1; while (n >= 32) { text.Append((char)((32 | (n & 31)) + 63)); n >>= 5; } text.Append((char)(n + 63)); }
    }
    private sealed class RouteHandler(string encoded) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<Task>? BeforeResponse { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; if (BeforeResponse is { } before) await before(); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { routes = new[] { new { distanceMeters = 100, duration = "100s", polyline = new { encodedPolyline = encoded } } } })) }; }
    }
    private sealed record AdminActor(int? UserId) : ICurrentUserContext;
    private sealed class Lifetime : IHostApplicationLifetime
    { public CancellationToken ApplicationStarted => default; public CancellationToken ApplicationStopping => default; public CancellationToken ApplicationStopped => default; public void StopApplication() { } }
    private sealed record Event(string Name, string[] Targets);
    private sealed class AdminRecordingHub<T> : IHubContext<T> where T : Hub
    { public List<Event> Events { get; } = []; public IHubClients Clients => new RecordingClients(Events); public IGroupManager Groups => throw new NotSupportedException(); }
    private sealed class Proxy(List<Event> events, string[] targets) : IClientProxy
    { public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) { events.Add(new(method, targets)); return Task.CompletedTask; } }
    private sealed class RecordingClients(List<Event> events) : IHubClients
    {
        public IClientProxy All => new Proxy(events, ["all"]);
        public IClientProxy AllExcept(IReadOnlyList<string> ids) => All;
        public IClientProxy Client(string id) => new Proxy(events, [id]);
        public IClientProxy Clients(IReadOnlyList<string> ids) => new Proxy(events, ids.ToArray());
        public IClientProxy Group(string id) => Client(id);
        public IClientProxy GroupExcept(string id, IReadOnlyList<string> ids) => Group(id);
        public IClientProxy Groups(IReadOnlyList<string> ids) => Clients(ids);
        public IClientProxy User(string id) => Client(id);
        public IClientProxy Users(IReadOnlyList<string> ids) => Clients(ids);
    }
}
