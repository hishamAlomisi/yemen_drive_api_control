using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using YemenDrive.Application.Rides;
using YemenDrive.Application.Support;
using YemenDrive.Application.Safety;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;
using OfferEntity = YemenDrive.Database.Entities.RideOffer;

var databaseName = "YemenDrive_MovementTests_" + Guid.NewGuid().ToString("N");
var options = new DbContextOptionsBuilder<YemenDriveDbContext>()
    .UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True").Options;
var settingsFile = Path.Combine(Path.GetTempPath(), databaseName + ".json");
var config = new DatabaseConfigurationStore(settingsFile);
await config.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }, default);
var count = 0;
try
{
    await using var db = new YemenDriveDbContext(options);
    await db.GetService<IMigrator>().MigrateAsync("20261104110000_RideMessageDeliveryReceipts");
    await db.Database.MigrateAsync();
    Check(!db.Database.HasPendingModelChanges(), "movement migration snapshot matches model");
    Check(!await db.RideMovementSnapshots.AnyAsync() && !await db.RideRequestDriverLocations.AnyAsync(), "migration does not fabricate historical locations");

    var admin = new User { PhoneNumber = "967700881001", DisplayName = "إدارة الحركة", Role = UserRole.Admin, IsSystemAdministrator = true };
    var limited = new User { PhoneNumber = "967700881002", DisplayName = "إدارة محدودة", Role = UserRole.Admin };
    var customer = new User { PhoneNumber = "967700881003", DisplayName = "راكب الحركة", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "967700881004", DisplayName = "سائق الحركة", Role = UserRole.Driver };
    var otherDriver = new User { PhoneNumber = "967700881005", DisplayName = "سائق آخر", Role = UserRole.Driver };
    var kind = new ServiceKind { Code = "movement-test", NameAr = "اختبار الحركة" };
    db.AddRange(admin, limited, customer, driver, otherDriver, kind); await db.SaveChangesAsync();
    var service = new RideServiceCatalogItem { Code = "movement-car", NameAr = "سيارة", DescriptionAr = "اختبار", ServiceKindId = kind.Id };
    db.Add(service); await db.SaveChangesAsync();
    var now = DateTime.UtcNow;
    var day = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
    var ride = new RideEntity { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
        Status = RideStatus.Completed, CreatedAtUtc = day.AddHours(10), DriverArrivedAtUtc = day.AddHours(10).AddMinutes(7),
        StartedAtUtc = day.AddHours(10).AddMinutes(9), ServiceEndedAtUtc = day.AddHours(10).AddMinutes(30), CompletedAtUtc = day.AddHours(10).AddMinutes(32),
        PickupLatitude = 15.36, PickupLongitude = 44.19, DestinationLatitude = 15.38, DestinationLongitude = 44.20,
        PickupLabel = "الانطلاق", DestinationLabel = "الوجهة", RouteQuotedDurationSeconds = 1000 };
    var legacy = new RideEntity { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
        Status = RideStatus.Cancelled, CreatedAtUtc = day.AddHours(9), StartedAtUtc = day.AddHours(9).AddMinutes(1), UpdatedAtUtc = day.AddHours(11) };
    var other = new RideEntity { CustomerId = customer.Id, DriverId = otherDriver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
        Status = RideStatus.Cancelled, CreatedAtUtc = day.AddHours(12), ServiceEndedAtUtc = day.AddHours(12).AddMinutes(5) };
    var boundary = new RideEntity { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id,
        Status = RideStatus.Cancelled, CreatedAtUtc = day.AddDays(1), ServiceEndedAtUtc = day.AddDays(1).AddMinutes(5) };
    db.AddRange(ride, legacy, other, boundary); await db.SaveChangesAsync();
    var accepted = day.AddHours(10).AddMinutes(2);
    db.RideOffers.Add(new OfferEntity { RideId = ride.Id, DriverId = driver.Id, Status = OfferStatus.Accepted, Amount = 100, UpdatedAtUtc = accepted, ExpiresAtUtc = accepted.AddMinutes(1) });
    db.RideMovementSnapshots.Add(new RideMovementSnapshot { RideId = ride.Id, RequestedAtUtc = ride.CreatedAtUtc, AcceptedAtUtc = accepted,
        PickupLatitude = ride.PickupLatitude, PickupLongitude = ride.PickupLongitude, PickupLabel = ride.PickupLabel,
        DestinationLatitude = ride.DestinationLatitude, DestinationLongitude = ride.DestinationLongitude, DestinationLabel = ride.DestinationLabel,
        PassengerLatitude = 15.359, PassengerLongitude = 44.189, PassengerAccuracy = 8, PassengerObservedAtUtc = ride.CreatedAtUtc.AddSeconds(-2),
        PlannedDurationSeconds = 1000 });
    db.RideRequestDriverLocations.Add(new() { RideId = ride.Id, DriverId = driver.Id, Latitude = 15.35, Longitude = 44.18,
        ObservedAtUtc = ride.CreatedAtUtc.AddSeconds(-1), SentAtUtc = ride.CreatedAtUtc });
    db.DriverRatings.Add(new() { RideId = ride.Id, DriverUserId = driver.Id, CustomerUserId = customer.Id, Source = DriverRatingSource.Customer, Score = 4, Comment = "وصل بأمان" });
    db.RideWaitingSessions.Add(new() { RideId = ride.Id, RequestedByUserId = customer.Id, RequestKey = Guid.NewGuid(), IsOpen = false,
        StartedAtUtc = ride.StartedAtUtc!.Value.AddMinutes(2), EndedAtUtc = ride.StartedAtUtc.Value.AddMinutes(5) });
    db.SupportTickets.Add(new() { RideId = ride.Id, UserId = customer.Id, Category = "General", Message = "بلاغ الرحلة", Status = "Open" });
    db.SupportTickets.Add(new() { RideId = other.Id, UserId = customer.Id, Category = "General", Message = "بلاغ آخر", Status = "Open" });
    db.SafetyIncidents.Add(new() { RideId = ride.Id, UserId = customer.Id, Message = "بلاغ سلامة", Status = SafetyIncidentStatus.Resolved });
    db.SafetyIncidents.Add(new() { RideId = other.Id, UserId = customer.Id, Message = "بلاغ سلامة آخر", Status = SafetyIncidentStatus.Open });
    db.LocationUpdates.AddRange(
        Point(ride, driver.Id, accepted.AddSeconds(-1), 15.34, 44.17),
        Point(ride, driver.Id, accepted, 15.35, 44.18),
        Point(ride, driver.Id, accepted.AddSeconds(30), 15.3501, 44.1801),
        Point(ride, driver.Id, ride.DriverArrivedAtUtc!.Value, 15.36, 44.19),
        Point(ride, driver.Id, ride.StartedAtUtc!.Value, 15.36, 44.19),
        Point(ride, driver.Id, ride.StartedAtUtc.Value.AddSeconds(30), 15.3601, 44.1901, "admin_simulation"),
        Point(ride, driver.Id, ride.ServiceEndedAtUtc!.Value, 15.38, 44.20),
        Point(ride, driver.Id, ride.ServiceEndedAtUtc.Value.AddSeconds(1), 20, 45),
        Point(ride, customer.Id, ride.StartedAtUtc.Value.AddSeconds(10), 16, 45),
        Point(ride, otherDriver.Id, ride.StartedAtUtc.Value.AddSeconds(10), 17, 46));
    await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    var report = new DriverMovementReport(db, config, new MovementActor(admin.Id));
    var filter = new { driverId = driver.Id, fromUtc = "2026-11-04T03:00:00+03:00", toUtc = "2026-11-05T03:00:00+03:00" };
    var rows = Ok(await Run(report, "list", filter));
    Check(rows.GetProperty("totalCount").GetInt32() == 2, "driver/date filter normalizes offsets and excludes exclusive upper bound");
    var first = rows.GetProperty("rows")[0];
    Check(first.GetProperty("rideId").GetInt32() == ride.Id && first.GetProperty("ratingScore").GetDecimal() == 4, "report includes agreed ride and customer rating");
    Check(first.GetProperty("arrivalDurationSeconds").GetInt64() == 300 && first.GetProperty("tripDurationSeconds").GetInt64() == 1260, "arrival starts at acceptance and trip ends at service end not later payment");
    Check(first.GetProperty("tripDelayAssessed").GetBoolean() && first.GetProperty("tripDelaySeconds").GetInt64() == 260, "completed unchanged trip compares against saved quote duration");
    Check(!first.GetProperty("arrivalDelayAssessed").GetBoolean(), "arrival lateness is unknown without saved ETA");
    Check(first.GetProperty("delayCount").GetInt32() == 1 && first.GetProperty("delayDurationSeconds").GetInt64() == 180, "report retains recorded waiting separately from estimated excess");
    Check(first.GetProperty("complaintCount").GetInt32() == 2 && first.GetProperty("openComplaintCount").GetInt32() == 1, "report counts support and safety cases scoped to ride");
    Check(first.GetProperty("createdAtUtc").GetString()!.EndsWith('Z'), "report serializes stored timestamps as UTC");
    Check(rows.GetProperty("rows")[1].GetProperty("arrivalDurationSeconds").ValueKind == JsonValueKind.Null, "legacy missing acceptance remains unknown");
    Check(rows.GetProperty("rows")[1].GetProperty("tripDurationSeconds").ValueKind == JsonValueKind.Null, "legacy missing service end does not use unrelated updated time as trip duration");
    var page = Ok(await Run(report, "list", new { driverId = driver.Id, pageSize = 1, page = 999 }));
    Check(page.GetProperty("page").GetInt32() == 3 && page.GetProperty("rows").GetArrayLength() == 1, "pagination clamps past final page without dropping rows");
    Fail(await Run(report, "list", new { pageSize = 101 }), "invalid_report_filter", "report rejects oversized page");
    Fail(await Run(new DriverMovementReport(db, config, new MovementActor(customer.Id)), "list", filter), "administrator_permission_required", "customer denied movement report");
    Fail(await Run(new DriverMovementReport(db, config, new MovementActor(limited.Id)), "search", new { }), "administrator_permission_required", "limited admin denied driver lookup");
    Check(Ok(await Run(report, "search", new { search = "سائق الحركة" })).GetArrayLength() == 1, "driver lookup filters independently of personal-data permission");
    var history = new RideMovementHistory(db, config, new MovementActor(admin.Id));
    var map = Ok(await Run(history, "get", new { rideId = ride.Id }));
    Check(map.GetProperty("approachSamples").GetArrayLength() == 3 && map.GetProperty("tripSamples").GetArrayLength() == 3, "map separates pickup and trip samples and excludes other actors and out-of-service points");
    Check(map.GetProperty("approachSegments").GetArrayLength() == 2 && map.GetProperty("tripSegments").GetArrayLength() == 2, "map breaks long GPS gaps instead of drawing fabricated travel");
    Check(map.GetProperty("driverAtRequest").GetProperty("latitude").GetDouble() == 15.35 && map.GetProperty("passengerAtRequest").GetProperty("latitude").GetDouble() == 15.359, "map uses immutable driver and real passenger request snapshots");
    Check(map.GetProperty("hasSimulatedSamples").GetBoolean(), "map identifies admin simulation samples");
    var old = Ok(await Run(history, "get", new { rideId = legacy.Id }));
    Check(!old.GetProperty("historyAvailable").GetBoolean() && old.GetProperty("driverAtRequest").ValueKind == JsonValueKind.Null && old.GetProperty("passengerAtRequest").ValueKind == JsonValueKind.Null, "old rides show missing history without substituting current GPS or pickup");
    Fail(await Run(new RideMovementHistory(db, config, new MovementActor(customer.Id)), "get", new { rideId = ride.Id }), "administrator_permission_required", "customer denied historical operations map");
    var support = new SupportTicketReport(db, config, new MovementActor(admin.Id));
    Check(Ok(await Run(support, "list", new { rideId = ride.Id })).GetArrayLength() == 1, "complaint drilldown filters support by ride");
    Check(Ok(await Run(new SafetyAdminReport(db, config, new MovementActor(admin.Id)), "list", new { rideId = ride.Id })).GetArrayLength() == 1, "complaint drilldown filters safety by ride");
    var loadedRide = await db.Rides.SingleAsync(x => x.Id == ride.Id); loadedRide.RouteVersion = 1;
    db.RideRouteChanges.Add(new() { RideId = ride.Id, RequestKey = Guid.NewGuid(), RequestedByUserId = customer.Id, Status = "accepted", IsOpen = false,
        ChangeObservedAtUtc = ride.StartedAtUtc!.Value.AddMinutes(3), DecidedAtUtc = ride.StartedAtUtc.Value.AddMinutes(3), ExpiresAtUtc = ride.StartedAtUtc.Value.AddMinutes(4) });
    await db.SaveChangesAsync();
    var changed = Ok(await Run(report, "list", filter)).GetProperty("rows")[0];
    Check(changed.GetProperty("routeChanged").GetBoolean() && changed.GetProperty("routeChangeCount").GetInt32() == 1 && !changed.GetProperty("tripDelayAssessed").GetBoolean(), "accepted route change is reported and invalidates original duration comparison");

    var fresh = new RideEntity { CustomerId = customer.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id, Status = RideStatus.Searching,
        PickupLatitude = 15.36, PickupLongitude = 44.19, DestinationLatitude = 15.38, DestinationLongitude = 44.20, CustomerPrice = 100 };
    db.Add(fresh); await db.SaveChangesAsync();
    await RideMovementCapture.RequestAsync(db, fresh, new RideModel(PassengerLatitude: 15.359, PassengerLongitude: 44.189,
        PassengerAccuracy: 8, PassengerObservedAtUtc: new DateTimeOffset(now.AddSeconds(-2))), default);
    await db.SaveChangesAsync();
    await RideMovementCapture.RequestAsync(db, fresh, new RideModel(PassengerLatitude: 20, PassengerLongitude: 45,
        PassengerAccuracy: 8, PassengerObservedAtUtc: new DateTimeOffset(now)), default);
    await db.SaveChangesAsync();
    Check((await db.RideMovementSnapshots.AsNoTracking().SingleAsync(x => x.RideId == fresh.Id)).PassengerLatitude == 15.359, "request retries cannot rewrite passenger origin");
    db.DriverProfiles.Add(new() { UserId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id, IsAvailable = true, IsApproved = true, AcceptsNewRequests = true, ApprovedPassengerSeats = 4 });
    db.DriverLiveLocations.Add(new() { DriverId = driver.Id, Latitude = 15.35, Longitude = 44.18, IsOnline = true, ObservedAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync();
    var search = new RideSearchAction(db, config, new MovementActor(customer.Id));
    Ok(await Run(search, "update", new { rideId = fresh.Id }));
    Check(await db.RideRequestDriverLocations.AnyAsync(x => x.RideId == fresh.Id && x.DriverId == driver.Id), "search dispatcher persists each invited driver position");
    var live = await db.DriverLiveLocations.SingleAsync(x => x.DriverId == driver.Id); live.Latitude = 15.351;
    await db.SaveChangesAsync();
    Ok(await Run(search, "update", new { rideId = fresh.Id }));
    Check((await db.RideRequestDriverLocations.SingleAsync(x => x.RideId == fresh.Id && x.DriverId == driver.Id)).Latitude == 15.35, "repeat invitations preserve first driver request position");
    var offer = new OfferEntity { RideId = fresh.Id, DriverId = driver.Id, Status = OfferStatus.Pending, Amount = 100, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1) };
    db.Add(offer); await db.SaveChangesAsync();
    Ok(await Run(new YemenDrive.Application.Rides.RideOffer(db, config, new MovementActor(customer.Id)), "accept", new { id = offer.Id }));
    Check((await db.RideMovementSnapshots.AsNoTracking().SingleAsync(x => x.RideId == fresh.Id)).AcceptedAtUtc != null, "real offer acceptance stores immutable acceptance time");
    await AdminLocationScenarios.RunAsync(options, config, Check);
    await MovementHttpScenarios.RunAsync(options, config, Check);
    Console.WriteLine($"PASS: {count} driver movement checks; isolated LocalDB and real HTTP/SignalR.");
}
finally
{
    if (!databaseName.StartsWith("YemenDrive_MovementTests_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test database name.");
    await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
    File.Delete(settingsFile);
}

void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
void Fail(ApiResult result, string code, string message) => Check(!result.Success && result.Code == code, message + " [" + result.Code + "]");
JsonElement Ok(ApiResult result) { if (!result.Success) throw new Exception(result.Code + ": " + result.Message); return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
static Task<ApiResult> Run(IModelService service, string operation, object model) => service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model, new JsonSerializerOptions(JsonSerializerDefaults.Web)), default);
static LocationUpdate Point(RideEntity ride, int actor, DateTime time, double lat, double lon, string source = "gps") =>
    new() { RideId = ride.Id, ActorId = actor, ObservedAtUtc = time, Latitude = lat, Longitude = lon, Accuracy = 5, Source = source };
public sealed record MovementActor(int? UserId) : ICurrentUserContext;
