using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.DriverPayments;
using YemenDrive.Application.Maps;
using YemenDrive.Application.Pricing;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;

internal static class RouteChangeScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int customer, int driver, int admin, int kind, int catalog)
    {
        var now = DateTime.UtcNow.AddSeconds(-2);
        var ride = new RideEntity { CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = catalog,
            Status = RideStatus.InProgress, StartedAtUtc = now.AddMinutes(-10), HasRoutePricingSnapshot = true,
            RouteSegmentStartedAtUtc = now.AddMinutes(-10), RouteSegmentLatitude = 15, RouteSegmentLongitude = 44,
            RouteBaseFare = 500, RoutePerKilometer = 100, RoutePerMinute = 20,
            RouteQuotedDistanceMeters = 10000, RouteQuotedDurationSeconds = 1200, RouteOriginalFare = 1520,
            RouteChangeFee = 200, CustomerPrice = 1520, ServiceFee = 100, WaitingFee = 50,
            AppliedDriverCommissionRate = .1m, DestinationLabel = "الوجهة الأصلية", DestinationLatitude = 15.2, DestinationLongitude = 44.2 };
        RidePricingPolicy.UpdateTotals(ride);
        db.Rides.Add(ride); await db.SaveChangesAsync();
        for (var i = 0; i <= 10; i++) db.LocationUpdates.Add(new LocationUpdate { RideId = ride.Id, ActorId = driver,
            Latitude = 15 + i * .001, Longitude = 44, Speed = 2, Accuracy = 5, ObservedAtUtc = now.AddMinutes(-10 + i) });
        var location = await db.DriverLiveLocations.SingleAsync(x => x.DriverId == driver);
        location.Latitude = 15.01; location.Longitude = 44; location.Speed = 0; location.IsOnline = true; location.ObservedAtUtc = now;
        db.RideWaitingSessions.Add(new RideWaitingSession { RideId = ride.Id, RequestedByUserId = customer, ApprovedByUserId = driver,
            RequestKey = Guid.NewGuid(), IsOpen = false, StartedAtUtc = now.AddMinutes(-6), EndedAtUtc = now.AddMinutes(-4), Fee = 50,
            PricePerMinute = 25, MaxFee = 500, EndReason = "driver_resumed" });
        await db.SaveChangesAsync();
        var routeStub = new RouteStub();
        var maps = new GoogleMapsGateway(new HttpClient(routeStub), new GoogleMapsOptions { RoutesApiKey = "test-only" }, NullLogger<GoogleMapsGateway>.Instance);
        var passenger = new RideRouteChangeService(db, config, new Actor(customer), maps);
        var driverService = new RideRouteChangeService(db, config, new Actor(driver), maps);
        var quote = new RideRouteChangeModel(ride.Id, RequestKey: Guid.NewGuid(), NewDestinationLatitude: 15.3, NewDestinationLongitude: 44.3, NewDestinationLabel: "المسار الجديد");
        var response = Data(await Execute(passenger, "quote", quote));
        var first = await db.RideRouteChanges.SingleAsync(x => x.RideId == ride.Id);
        var action = new RideRouteChangeModel(ride.Id, first.Id);
        Is(ride.DestinationLabel == "الوجهة الأصلية" && ride.TotalAmount == 1670 && first.Status == "quoted", "preview cannot change route or charge");
        Is(first.CompletedDistanceMeters > 1110 && first.CompletedDistanceMeters < 1113 && first.CompletedDurationSeconds == 480, "GPS distance measured and waiting time excluded");
        Is(first.CompletedFare == 617 && first.NewRouteFare == 560 && first.NewTotal == 1527, "negotiated factor applied to actual travelled and new route with one base/service fee");
        Ok(await Execute(passenger, "quote", quote));
        Is(routeStub.Requests == 1 && await db.RideRouteChanges.CountAsync(x => x.RideId == ride.Id) == 1, "quote retry is idempotent");
        Is(!(await Execute(passenger, "quote", quote with { NewDestinationLatitude = 16 })).Success, "request key bound to destination");
        var otherView = Data(await Execute(driverService, "get", new(ride.Id)));
        Is(otherView.GetProperty("history").GetArrayLength() == 0, "private preview stays private before send");
        Is(!(await Execute(driverService, "accept", action)).Success, "other participant cannot accept an unsent preview");
        Ok(await Execute(passenger, "add", action));
        Is(!(await Execute(passenger, "accept", action)).Success, "requester cannot approve themselves");
        Is(!(await Execute(new RideRouteChangeService(db, config, new Actor(admin), maps), "accept", action)).Success, "admin cannot bypass participant consent");
        var accounting = new RideAccountingPostingService(db, new AccountingPostingService(db));
        var collection = new DriverCashPayment(db, config, new Actor(driver), accounting);
        Is(!(await Execute(collection, "add", new DriverCashPaymentModel(ride.Id, 1670, "YER", null, "route-pending"))).Success, "pending route blocks collection");
        Is(!(await Execute(new YemenDrive.Application.Rides.Ride(db, config, new Actor(driver), accounting), "update", new RideModel(Id: ride.Id, CustomerPaymentEnabled: true))).Success, "pending route blocks opening payment");
        var waiting = new RideWaiting(db, config, new Actor(driver), new RideWaitingCoordinator(db));
        Is(!(await Execute(waiting, "add", new RideWaitingModel(ride.Id, RequestKey: Guid.NewGuid()))).Success, "pending route blocks waiting");
        Ok(await Execute(driverService, "accept", action));
        Ok(await Execute(driverService, "accept", action));
        Is(ride.RouteVersion == 1 && ride.RouteChangeFees == 200 && ride.TotalAmount == 1527 && ride.DestinationLabel == "المسار الجديد", "accept changes route and accounting exactly once");
        Is(ride.DriverShare + ride.PlatformShare == ride.TotalAmount && ride.RoutePolyline == "encoded-route", "financial shares and route updated together");

        routeStub.Distance = 1000; routeStub.Duration = 120;
        Ok(await Execute(driverService, "quote", quote with { RequestKey = Guid.NewGuid(), NewDestinationLatitude = 15.4, NewDestinationLabel = "وجهة أقرب" }));
        var second = await db.RideRouteChanges.Where(x => x.RideId == ride.Id).OrderByDescending(x => x.Id).FirstAsync();
        var secondAction = new RideRouteChangeModel(ride.Id, second.Id);
        Ok(await Execute(driverService, "add", secondAction));
        Ok(await Execute(passenger, "accept", secondAction));
        Is(ride.RouteVersion == 2 && ride.RouteCompletedFare == 617 && ride.RouteChangeFees == 400 && ride.TotalAmount == 1279, "successive shorter route preserves completed part; no duplicate base or old remaining fare");
        var report = Data(await Execute(new RideRouteChangeReport(db, config, new Actor(admin)), "report", new RideRouteChangeReportModel(RideId: ride.Id, PageSize: 1)));
        Is(report.GetProperty("totalCount").GetInt32() == 2 && report.GetProperty("summary").GetProperty("chargedFee").GetDecimal() == 400, "admin report aggregates accepted change fees across pages without duplication");
        Is(!(await Execute(new RideRouteChangeReport(db, config, new Actor(customer)), "report", new RideRouteChangeReportModel())).Success, "report admin only");

        async Task<RideRouteChange> Preview()
        {
            Ok(await Execute(passenger, "quote", quote with { RequestKey = Guid.NewGuid(), NewDestinationLatitude = 15.5 }));
            return await db.RideRouteChanges.Where(x => x.RideId == ride.Id).OrderByDescending(x => x.Id).FirstAsync();
        }
        var rejected = await Preview();
        Ok(await Execute(passenger, "add", new(ride.Id, rejected.Id)));
        Ok(await Execute(driverService, "reject", new(ride.Id, rejected.Id)));
        Is(ride.RouteVersion == 2 && ride.TotalAmount == 1279, "rejection retains original accepted route/fare");
        var stale = await Preview();
        Ok(await Execute(passenger, "add", new(ride.Id, stale.Id)));
        location.Latitude += .002; await db.SaveChangesAsync();
        Is(!(await Execute(driverService, "accept", new(ride.Id, stale.Id))).Success && stale.Status == "expired", "moving beyond quote tolerance requires fresh consent");
        location.Latitude = 15.01; await db.SaveChangesAsync();
        var expired = await Preview();
        expired.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        await RideRouteChangePolicy.ExpireAsync(db, ride, DateTime.UtcNow, CancellationToken.None); await db.SaveChangesAsync();
        Is(!expired.IsOpen && expired.Status == "expired", "worker expires abandoned quote without charging");
        var pendingWait = new RideWaitingSession { RideId = ride.Id, RequestKey = Guid.NewGuid(), RequestedByUserId = driver };
        db.RideWaitingSessions.Add(pendingWait); await db.SaveChangesAsync();
        Is(!(await Execute(passenger, "quote", quote with { RequestKey = Guid.NewGuid() })).Success, "open waiting blocks repricing");
        pendingWait.IsOpen = false; pendingWait.EndReason = "withdrawn"; await db.SaveChangesAsync();
        // Every paid collection is made against the final authoritative total.
        Ok(await Execute(collection, "add", new DriverCashPaymentModel(ride.Id, 1279, "YER", null, "route-final")));
        Is(await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && x.Amount == 1279 && x.Status == PaymentStatus.Paid), "collection includes both changes and waiting exactly once");
        Is(!(await Execute(passenger, "quote", quote with { RequestKey = Guid.NewGuid() })).Success, "paid ride cannot be repriced");
        Console.WriteLine("PASS: route preview/consent, GPS travelled fare, waiting exclusion, negotiated rate, successive changes, stale/expired/rejected requests, payment locks, report and collection.");
    }
    private static Task<ApiResult> Execute<T>(ModelService<T> service, string operation, T model) where T : class => service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    private static JsonElement Data(ApiResult result) { Ok(result); return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
    private static void Ok(ApiResult result) => Is(result.Success, result.Code + ": " + result.Message);
    private static void Is(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
    private sealed class RouteStub : HttpMessageHandler
    {
        public int Requests; public int Distance = 5000; public int Duration = 600;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"routes\":[{{\"distanceMeters\":{Distance},\"duration\":\"{Duration}s\",\"polyline\":{{\"encodedPolyline\":\"encoded-route\"}}}}]}}") }); }
    }
}
