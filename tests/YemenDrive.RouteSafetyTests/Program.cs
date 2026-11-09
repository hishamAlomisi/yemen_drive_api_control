using System.Net;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api.Safety;
using YemenDrive.Application.Maps;
using YemenDrive.Application.Pricing;
using YemenDrive.Application.Rides;
using YemenDrive.Application.Safety;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using Ride = YemenDrive.Database.Entities.Ride;
using PricingRule = YemenDrive.Database.Entities.PricingRule;

var databaseName = "YemenDrive_RouteSafetyTests_" + Guid.NewGuid().ToString("N");
var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(
    $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True").Options;
var settingsPath = Path.Combine(Path.GetTempPath(), databaseName + ".json");
var config = new DatabaseConfigurationStore(settingsPath);
await config.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB",
    Database = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }, default);
var now = DateTime.UtcNow; var clock = new SafetyClock(now); var count = 0;
var route = Encode([(0, 0), (0, .01)]);
var provider = new SafetyMapsHandler(route);
using var client = new HttpClient(provider);
var maps = new GoogleMapsGateway(client, new GoogleMapsOptions { RoutesApiKey = "isolated-test" }, NullLogger<GoogleMapsGateway>.Instance);
try
{
    await using var db = new YemenDriveDbContext(options);
    await db.GetService<IMigrator>().MigrateAsync("20261105140400_DriverMovementHistory");
    await db.Database.MigrateAsync();
    Check(!db.Database.HasPendingModelChanges(), "migration matches model and upgrades previous deployed schema");
    var customer = new User { Role = UserRole.Customer, DisplayName = "راكب", PhoneNumber = "safety-rider" };
    var driver = new User { Role = UserRole.Driver, DisplayName = "سائق", PhoneNumber = "safety-driver" };
    var outsider = new User { Role = UserRole.Customer, DisplayName = "آخر", PhoneNumber = "safety-other" };
    var admin = new User { Role = UserRole.Admin, IsSystemAdministrator = true, DisplayName = "الإدارة", PhoneNumber = "safety-admin" };
    var kind = new ServiceKind { Code = "safety", NameAr = "سلامة" };
    db.AddRange(customer, driver, outsider, admin, kind); await db.SaveChangesAsync();
    var catalog = new RideServiceCatalogItem { ServiceKindId = kind.Id, Code = "safety-car", NameAr = "سيارة", DescriptionAr = "اختبار" };
    db.Add(catalog); await db.SaveChangesAsync();
    var ride = new Ride { CustomerId = customer.Id, DriverId = driver.Id, Status = RideStatus.InProgress,
        StartedAtUtc = now.AddMinutes(-5), ServiceKindId = kind.Id, ServiceCatalogItemId = catalog.Id,
        PickupLabel = "الانطلاق", PickupAddress = "عنوان الانطلاق", PickupLatitude = 0, PickupLongitude = 0,
        DestinationLabel = "الوجهة الكاملة", DestinationAddress = "عنوان الوجهة", DestinationLatitude = 0, DestinationLongitude = .01,
        RoutePolyline = route };
    db.Add(ride); await db.SaveChangesAsync();
    var service = new RouteDeviationService(db, maps, timeProvider: clock);

    Check(!RouteDeviationGeometry.IsDeviating(19.9999) && RouteDeviationGeometry.IsDeviating(20) &&
        RouteDeviationGeometry.IsDeviating(20.001), "20 metre threshold is inclusive without rounded-distance false positives");
    Check(RouteDeviationGeometry.Measure("malformed_", 0, 0) is null &&
        RouteDeviationGeometry.Measure(Encode([(0, 0)]), 0, 0) is null, "malformed and degenerate road geometry unavailable");
    Check(RouteDeviationGeometry.Measure(route, double.NaN, 0) is null, "nonfinite GPS unavailable");
    var twenty = RouteDeviationGeometry.Measure(route, 20 / 111194.92664455874, .005)!;
    Check(Math.Abs(twenty.DistanceMeters - 20) < .0001 && twenty.Direction == "الشمال", "perpendicular metres and compass bearing are measured on route segments");
    var corner = Encode([(0, 0), (.01, 0), (.01, .01)]);
    Check(RouteDeviationGeometry.Measure(corner, .005, .005)!.DistanceMeters > 500, "whole L-shaped route is respected instead of endpoint chord");
    Check(RouteDeviationGeometry.Measure(route, 0, -.0003)!.Direction == "الغرب", "distance past the route endpoint has a real westward direction");

    await Denied(() => service.GetAsync(ride.Id, outsider.Id, default), "ride_access_denied");
    await Denied(() => service.ReportAsync(ride.Id, driver.Id, default), "ride_access_denied");
    Check((await service.GetAsync(ride.Id, customer.Id, default)).ReasonCode == "location_unavailable", "no GPS remains unknown rather than on-route");
    await Point(0, .005, now.AddSeconds(-3));
    var onRoute = await service.ReportAsync(ride.Id, customer.Id, default);
    Check(onRoute.Available && !onRoute.IsDeviating && !onRoute.Accepted && !await db.SafetyIncidents.AnyAsync(), "manual on-route report is rejected without incident or notifications");
    await Point(.0005, .005, now.AddSeconds(-2));
    var detected = await service.GetAsync(ride.Id, customer.Id, default);
    Check(detected.Available && detected.IsDeviating && detected.DistanceMeters is > 55 and < 56 &&
        detected.Direction == "الشمال", "fresh GPS 55m north triggers deviation with distance and direction");
    var accepted = await service.ReportAsync(ride.Id, customer.Id, default);
    Check(accepted.Accepted && accepted.IncidentId > 0 && !accepted.Duplicate, "verified deviation creates accepted safety report");
    var incident = await db.SafetyIncidents.SingleAsync();
    Check(incident.Kind == "route_deviation" && incident.VerifiedRoutePolyline == route &&
        incident.RouteVersion == 0 && incident.LocationSource == "driverGps" && incident.Latitude == .0005 &&
        incident.LocationAccuracyMeters == 5 && incident.DeviationDirection == "الشمال", "report preserves trusted route, version, GPS, accuracy, metres and direction for audit");
    Check(await db.Notifications.CountAsync() == 2 && await db.Notifications.AnyAsync(x =>
        x.UserId == driver.Id && x.Type == NotificationType.Safety && x.DataJson!.Contains("route_deviation_reported")) &&
        await db.Notifications.AnyAsync(x => x.UserId == admin.Id && x.DataJson!.Contains("open_safety_incident")), "admin and driver receive durable safety notifications");
    Check((await db.Rides.AsNoTracking().SingleAsync(x => x.Id == ride.Id)).Status == RideStatus.InProgress, "report leaves trip in progress");
    var duplicate = await service.ReportAsync(ride.Id, customer.Id, default);
    Check(duplicate.Accepted && duplicate.Duplicate && duplicate.IncidentId == accepted.IncidentId &&
        await db.SafetyIncidents.CountAsync() == 1 && await db.Notifications.CountAsync() == 2, "repeated report is idempotent within notification cooldown");
    var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ => {
        await using var separate = new YemenDriveDbContext(options);
        return await new RouteDeviationService(separate, maps, timeProvider: clock).ReportAsync(ride.Id, customer.Id, default);
    }));
    Check(results.All(x => x.Duplicate && x.IncidentId == accepted.IncidentId) && await db.SafetyIncidents.CountAsync() == 1,
        "parallel submissions serialize and do not duplicate alerts");

    await Point(0, .005, now.AddSeconds(-1));
    Check(!(await service.ReportAsync(ride.Id, customer.Id, default)).Accepted, "server rechecks latest returned-to-route GPS after warning and rejects stale client confirmation");
    await Point(.0005, .005, now);
    var shifted = Encode([(.0005, 0), (.0005, .01)]);
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.RouteVersion, 1)
        .SetProperty(x => x.RoutePolyline, shifted).SetProperty(x => x.RouteSegmentStartedAtUtc, now.AddSeconds(-1)));
    var reroute = await service.ReportAsync(ride.Id, customer.Id, default);
    Check(reroute.RouteVersion == 1 && !reroute.IsDeviating && !reroute.Accepted, "approved destination route supersedes old route before report verification");
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.RoutePolyline, route));
    var secondRoute = await service.ReportAsync(ride.Id, customer.Id, default);
    Check(secondRoute.Accepted && !secondRoute.Duplicate && secondRoute.IncidentId != accepted.IncidentId, "changed route version can create independent audited report");
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.RouteVersion, 2));
    var initialConcurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ => {
        await using var separate = new YemenDriveDbContext(options);
        return await new RouteDeviationService(separate, maps, timeProvider: clock).ReportAsync(ride.Id, customer.Id, default);
    }));
    Check(initialConcurrent.Count(x => !x.Duplicate) == 1 && initialConcurrent.Select(x => x.IncidentId).Distinct().Count() == 1 &&
        await db.SafetyIncidents.CountAsync(x => x.RouteVersion == 2) == 1, "concurrent first reports create exactly one incident and notification pair");
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.RouteVersion, 3));
    var failingRealtime = DispatchProxy.Create<IRideRealtimePublisher, ThrowingSafetyRealtimeProxy>();
    var resilient = await new RouteDeviationService(db, maps, failingRealtime, clock).ReportAsync(ride.Id, customer.Id, default);
    Check(resilient.Accepted && await db.SafetyIncidents.AnyAsync(x => x.Id == resilient.IncidentId),
        "post-commit realtime outage does not turn a persisted report into an unknown outcome");
    await db.LocationUpdates.Where(x => x.RideId == ride.Id).ExecuteDeleteAsync();
    await Point(.0005, .005, now.AddSeconds(-31));
    Check((await service.GetAsync(ride.Id, customer.Id, default)).ReasonCode == "location_stale", "stale driver GPS cannot accuse a deviation");
    await Point(.0005, .005, now.AddSeconds(6));
    Check(!(await service.ReportAsync(ride.Id, customer.Id, default)).Accepted, "future GPS cannot authorize report");
    await db.LocationUpdates.Where(x => x.RideId == ride.Id).ExecuteDeleteAsync();
    await Point(.0005, .005, now, accuracy: 21);
    Check((await service.ReportAsync(ride.Id, customer.Id, default)).ReasonCode == "location_inaccurate", "poor GPS accuracy cannot authorize 20m report");
    await db.LocationUpdates.Where(x => x.RideId == ride.Id).ExecuteDeleteAsync();
    await Point(.0005, .005, now, source: "admin_simulation");
    Check((await service.ReportAsync(ride.Id, customer.Id, default)).ReasonCode == "location_not_gps", "admin simulation does not create genuine driver safety complaint");
    await db.LocationUpdates.Where(x => x.RideId == ride.Id).ExecuteDeleteAsync();
    await Point(.0005, .005, now);
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.Status, RideStatus.DriverEnRoute));
    Check((await service.ReportAsync(ride.Id, customer.Id, default)).ReasonCode == "ride_not_in_progress", "prepickup travel is excluded from destination-route complaint");
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.Status, RideStatus.Completed));
    Check(!(await service.ReportAsync(ride.Id, customer.Id, default)).Accepted, "completed ride cannot be reported through live deviation endpoint");
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.Status, RideStatus.InProgress)
        .SetProperty(x => x.RouteVersion, 0).SetProperty(x => x.RoutePolyline, (string?)null));
    var legacy = await service.GetAsync(ride.Id, customer.Id, default);
    Check(legacy.IsDeviating && provider.Requests.Count == 1 &&
        provider.Requests[0].GetProperty("origin").GetProperty("location").GetProperty("latLng").GetProperty("latitude").GetDouble() == 0,
        "legacy route is generated from original pickup, never from deviating driver GPS");
    await service.GetAsync(ride.Id, customer.Id, default);
    Check(provider.Requests.Count == 1 && (await db.Rides.AsNoTracking().SingleAsync(x => x.Id == ride.Id)).RoutePolyline == route,
        "legacy baseline is fixed once and reused for later samples");
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.RoutePolyline, (string?)null));
    provider.Fail = true;
    var unavailable = await service.ReportAsync(ride.Id, customer.Id, default);
    Check(!unavailable.Available && !unavailable.Accepted && unavailable.ReasonCode == "route_unavailable", "map provider failure never invents a straight route or falsely clears safety");
    var providerRequestsAfterFailure = provider.Requests.Count;
    await service.GetAsync(ride.Id, customer.Id, default);
    await service.GetAsync(ride.Id, customer.Id, default);
    Check(provider.Requests.Count == providerRequestsAfterFailure, "repeated status polling uses bounded failure cooldown instead of retrying maps indefinitely");
    provider.Fail = false;
    await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.RoutePolyline, route));

    var racedRide = new Ride { CustomerId = customer.Id, DriverId = driver.Id, Status = RideStatus.InProgress,
        StartedAtUtc = now.AddMinutes(-5), PickupLatitude = 0, PickupLongitude = 0,
        DestinationLatitude = 0, DestinationLongitude = .01, ServiceKindId = kind.Id, ServiceCatalogItemId = catalog.Id };
    db.Rides.Add(racedRide); await db.SaveChangesAsync();
    provider.BeforeResponse = async () => { await db.Rides.Where(x => x.Id == racedRide.Id).ExecuteUpdateAsync(x =>
        x.SetProperty(x => x.RouteVersion, 1).SetProperty(x => x.RoutePolyline, shifted)); };
    await service.GetAsync(racedRide.Id, customer.Id, default);
    provider.BeforeResponse = null;
    Check((await db.Rides.AsNoTracking().SingleAsync(x => x.Id == racedRide.Id)).RoutePolyline == shifted,
        "route approved during legacy provider fetch cannot be overwritten by stale original route");
    await db.Rides.Where(x => x.Id == racedRide.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.Status, RideStatus.Completed)
        .SetProperty(x => x.ServiceEndedAtUtc, now));

    db.PricingRules.Add(new PricingRule { ServiceKindId = kind.Id, ServiceCatalogItemId = catalog.Id, IsActive = true,
        BaseFare = 100, MaxPriceDecreasePercent = 10, MaxPriceIncreasePercent = 10, DriverShareRate = .8m });
    await db.SaveChangesAsync();
    var priceResult = await new Pricing(db, config, new SafetyActor(customer.Id), maps).ExecuteAsync("report",
        JsonSerializer.SerializeToElement(new { serviceKindId = kind.Id, serviceCatalogItemId = catalog.Id,
            pickupLatitude = 0, pickupLongitude = 0, destinationLatitude = 0, destinationLongitude = .01 }), default);
    Check(priceResult.Success, "server quote creation succeeds");
    var quote = await db.RidePricingQuotes.SingleAsync();
    Check(quote.RoutePolyline == route, "new quote retains provider road polyline alongside contractual distance");
    var quotedRide = new Ride { CustomerId = customer.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = catalog.Id,
        PickupLatitude = 0, PickupLongitude = 0, DestinationLatitude = 0, DestinationLongitude = .01, CustomerPrice = 100 };
    await RidePricingPolicy.ApplyQuoteAsync(db, quotedRide, quote.Token, default);
    Check(quotedRide.RoutePolyline == route, "booking copies trusted quote polyline to ride before tracking starts");
    var adminResult = await new SafetyAdminReport(db, config, new SafetyActor(admin.Id)).ExecuteAsync("list",
        JsonSerializer.SerializeToElement(new { rideId = ride.Id }), default);
    var adminRows = JsonSerializer.SerializeToElement(adminResult.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(adminResult.Success && adminRows[0].GetProperty("kind").GetString() == "route_deviation" &&
        adminRows[0].GetProperty("locationSource").GetString() == "driverGps", "admin safety report exposes correct driver GPS evidence and report category");

    await PreviewChecks();
    await RouteSafetyHttpChecks.RunAsync(options, config, ride.Id, customer.Id, driver.Id, outsider.Id, route, Check);
    Console.WriteLine($"PASS: {count} route safety and driver preview checks.");

    async Task Point(double lat, double lng, DateTime time, double accuracy = 5, string source = "gps")
    { db.LocationUpdates.Add(new() { RideId = ride.Id, ActorId = driver.Id, Latitude = lat, Longitude = lng,
        ObservedAtUtc = time, Accuracy = accuracy, Source = source }); await db.SaveChangesAsync(); }
    async Task PreviewChecks()
    {
        await db.Rides.Where(x => x.Id == ride.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.Status, RideStatus.Completed)
            .SetProperty(x => x.ServiceEndedAtUtc, now));
        db.DriverProfiles.Add(new() { UserId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = catalog.Id,
            IsAvailable = true, AcceptsNewRequests = true, IsApproved = true, ApprovedPassengerSeats = 4 });
        db.DriverLiveLocations.Add(new() { DriverId = driver.Id, Latitude = 0, Longitude = 0, IsOnline = true, ObservedAtUtc = DateTime.UtcNow });
        var searching = new Ride { CustomerId = customer.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = catalog.Id,
            Status = RideStatus.Searching, PickupLatitude = 0, PickupLongitude = 0, DestinationLatitude = 0,
            DestinationLongitude = .01, DestinationLabel = "الوجهة الكاملة", DestinationAddress = "عنوان كامل",
            RoutePolyline = route, RouteQuotedDistanceMeters = 1234 };
        db.Add(searching); await db.SaveChangesAsync();
        var report = new RideReport(db, config, new SafetyActor(driver.Id));
        async Task<JsonElement> Rows()
        {
            var result = await report.ExecuteAsync("list", JsonSerializer.SerializeToElement(new { status = 1 }), default);
            if (!result.Success) throw new Exception(result.Code);
            return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        var preview = (await Rows())[0];
        Check(preview.GetProperty("destinationAvailable").GetBoolean() && preview.GetProperty("destinationLongitude").GetDouble() == .01 &&
            preview.GetProperty("destinationLabel").GetString() == "الوجهة الكاملة" && preview.GetProperty("tripDistanceMeters").GetDouble() == 1234,
            "eligible preoffer driver receives exact destination, route and distance");
        Check(preview.GetProperty("customerName").ValueKind == JsonValueKind.Null && preview.GetProperty("customerPhone").ValueKind == JsonValueKind.Null,
            "preview keeps customer identity and phone hidden until assignment");
        await db.DriverLiveLocations.Where(x => x.DriverId == driver.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.Latitude, 1));
        Check((await Rows()).GetArrayLength() == 0, "out-of-radius driver cannot retrieve destination preview");
        await db.DriverLiveLocations.Where(x => x.DriverId == driver.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.Latitude, 0));
        await db.DriverProfiles.Where(x => x.UserId == driver.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.IsApproved, false));
        Check((await Rows()).GetArrayLength() == 0, "unapproved driver cannot retrieve preview");
        await db.DriverProfiles.Where(x => x.UserId == driver.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.IsApproved, true));
        await db.Rides.Where(x => x.Id == searching.Id).ExecuteUpdateAsync(x => x.SetProperty(x => x.Status, RideStatus.Negotiating));
        var negotiating = await report.ExecuteAsync("list", JsonSerializer.SerializeToElement(new { status = 2 }), default);
        Check(JsonSerializer.SerializeToElement(negotiating.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))[0]
            .GetProperty("destinationAvailable").GetBoolean(), "negotiating preacceptance request retains route preview");
    }
}
finally
{
    if (!databaseName.StartsWith("YemenDrive_RouteSafetyTests_", StringComparison.Ordinal)) throw new Exception("Invalid test database scope.");
    await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
    File.Delete(settingsPath);
}
void Check(bool result, string message) { if (!result) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
async Task Denied(Func<Task<RouteDeviationStatus>> operation, string code)
{ try { await operation(); throw new Exception("Expected rejection " + code); } catch (ServiceException error) { Check(error.Code == code, "access rejected: " + code); } }
static string Encode((double Latitude, double Longitude)[] points)
{
    var builder = new StringBuilder(); int lat = 0, lng = 0;
    foreach (var point in points) { var a = (int)Math.Round(point.Latitude * 1e5); var b = (int)Math.Round(point.Longitude * 1e5); Value(a - lat); Value(b - lng); lat = a; lng = b; }
    return builder.ToString();
    void Value(int input) { var value = input < 0 ? ~(input << 1) : input << 1; while (value >= 32) { builder.Append((char)((32 | (value & 31)) + 63)); value >>= 5; } builder.Append((char)(value + 63)); }
}
internal sealed class SafetyClock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero); }
internal sealed record SafetyActor(int? UserId) : ICurrentUserContext;
internal sealed class SafetyMapsHandler(string polyline) : HttpMessageHandler
{
    public List<JsonElement> Requests { get; } = [];
    public bool Fail { get; set; }
    public Func<Task>? BeforeResponse { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement.Clone());
        if (BeforeResponse is not null) await BeforeResponse();
        if (Fail) return new(HttpStatusCode.ServiceUnavailable);
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { routes = new[] {
            new { distanceMeters = 1112, duration = "120s", polyline = new { encodedPolyline = polyline } } } }), Encoding.UTF8, "application/json") };
    }
}
public class ThrowingSafetyRealtimeProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new IOException("Simulated realtime outage after commit.");
}
