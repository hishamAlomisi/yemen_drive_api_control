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
using RideService = YemenDrive.Application.Rides.Ride;
using PricingRuleService = YemenDrive.Application.Pricing.PricingRule;
using PricingService = YemenDrive.Application.Pricing.Pricing;

internal static class PricingWaitingScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int customer, int driver, int admin, int kind, int catalog)
    {
        var ruleService = new PricingRuleService(db, config, new Actor(admin));
        var model = new PricingRuleModel(ServiceKindId: kind, ServiceCatalogItemId: catalog, BaseFare: 500, PerKilometer: 100,
            PerMinute: 20, ServiceFee: 100, DriverCommissionRate: .1m, WaitingFreeMinutes: 2, WaitingPricePerMinute: 50,
            WaitingMaxFee: 500, MaxPriceDecreasePercent: 20, MaxPriceIncreasePercent: 30, RouteChangeFee: 200);
        Ok(await Execute(ruleService, "add", model));
        var rule = await db.PricingRules.OrderByDescending(x => x.Id).FirstAsync();
        Is(!(await Execute(new PricingRuleService(db, config, new Actor(customer)), "update", model with { Id = rule.Id })).Success, "customer cannot change pricing policy");
        Is(!(await Execute(ruleService, "update", model with { Id = rule.Id, MaxPriceDecreasePercent = 101 })).Success, "invalid percentage rejected");
        Is(!(await Execute(ruleService, "update", model with { Id = rule.Id, WaitingMaxFee = 0 })).Success, "paid waiting requires a cap");
        Is(!(await Execute(ruleService, "update", model with { Id = rule.Id, RouteChangeFee = -.1m })).Success, "negative route fee rejected");
        Is(!(await Execute(ruleService, "update", model with { Id = rule.Id, RouteChangeFee = 1.5m })).Success, "fractional route fee rejected");
        Is(RidePricingPolicy.Bounds(1001, 20, 30) == (801m, 1301m), "whole-rial bounds round inward");
        var gateway = new GoogleMapsGateway(new HttpClient(new RouteStub()), new GoogleMapsOptions { RoutesApiKey = "test-only" }, NullLogger<GoogleMapsGateway>.Instance);
        var pricing = new PricingService(db, config, new Actor(customer), gateway);
        var priced = await Execute(pricing, "report", new PricingModel(kind, catalog, 99999, 99999, 15.36, 44.20, 15.38, 44.22));
        Ok(priced);
        var quote = await db.RidePricingQuotes.OrderByDescending(x => x.Id).FirstAsync();
        Is(quote.Amount == 1000 && quote.MinPrice == 800 && quote.MaxPrice == 1300, "quote uses trusted route instead of client distance");
        var accounting = new RideAccountingPostingService(db, new AccountingPostingService(db));
        var rideService = new RideService(db, config, new Actor(customer), accounting);
        var draft = new RideModel(ServiceKindId: kind, ServiceCatalogItemId: catalog, PickupAddress: "اختبار الانطلاق", DestinationAddress: "اختبار الوجهة",
            PickupLatitude: 15.36, PickupLongitude: 44.20, DestinationLatitude: 15.38, DestinationLongitude: 44.22,
            CustomerPrice: 1000, PricingQuoteToken: quote.Token, IdempotencyKey: "pricing-waiting-tests");
        Is(!(await Execute(rideService, "add", draft with { CustomerPrice = 799 })).Success, "create below bound rejected");
        Is(!(await Execute(rideService, "add", draft with { CustomerPrice = 1301 })).Success, "create above bound rejected");
        Is(!(await Execute(rideService, "add", draft with { DestinationLatitude = 16 })).Success, "quote cannot be reused for another route");
        Is(!(await Execute(rideService, "add", draft with { PricingQuoteToken = null })).Success, "missing quote cannot bypass limits");
        Ok(await Execute(rideService, "add", draft));
        var ride = await db.Rides.SingleAsync(x => x.IdempotencyKey == draft.IdempotencyKey);
        var search = new RideSearchAction(db, config, new Actor(customer));
        Is(!(await Execute(search, "update", new RideSearchActionModel(ride.Id, CustomerPrice: 1301))).Success, "search cannot exceed upper bound");
        Is(!(await Execute(rideService, "update", new RideModel(Id: ride.Id, CustomerPrice: 799))).Success, "legacy update cannot bypass lower bound");
        db.ChangeTracker.Clear();
        Ok(await Execute(search, "update", new RideSearchActionModel(ride.Id, CustomerPrice: 800)));
        Ok(await Execute(search, "update", new RideSearchActionModel(ride.Id, CustomerPrice: 1300)));
        Ok(await Execute(search, "update", new RideSearchActionModel(ride.Id, CustomerPrice: 1000)));
        Ok(await Execute(ruleService, "update", model with { Id = rule.Id, WaitingPricePerMinute = 99, MaxPriceIncreasePercent = 0, RouteChangeFee = 999 }));
        ride = await db.Rides.SingleAsync(x => x.Id == ride.Id);
        Is(ride.MaxCustomerPrice == 1300 && ride.WaitingPricePerMinute == 50, "existing ride retains its pricing policy");
        Is(ride.HasRoutePricingSnapshot && ride.RouteChangeFee == 200 && ride.RouteBaseFare == 500 && ride.RoutePerKilometer == 100 && ride.RouteQuotedDistanceMeters == 3000, "route rates/distance and change fee are snapshotted before later admin edits");
        ride.DriverId = driver; ride.Status = RideStatus.InProgress;
        RidePricingPolicy.UpdateTotals(ride);
        await SetLocation(db, driver, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var coordinator = new RideWaitingCoordinator(db);
        var passengerWaiting = new RideWaiting(db, config, new Actor(customer), coordinator);
        var driverWaiting = new RideWaiting(db, config, new Actor(driver), coordinator);
        var request = new RideWaitingModel(ride.Id, RequestKey: Guid.NewGuid());
        Ok(await Execute(driverWaiting, "add", request));
        Ok(await Execute(driverWaiting, "add", request));
        Is(await db.RideWaitingSessions.CountAsync(x => x.RideId == ride.Id) == 1, "repeated request is idempotent");
        var session = await db.RideWaitingSessions.SingleAsync(x => x.RideId == ride.Id);
        var action = new RideWaitingModel(ride.Id, session.Id);
        Is(session.StartedAtUtc is null, "waiting does not start before consent");
        Is(!(await Execute(driverWaiting, "accept", action)).Success, "requester cannot self-approve");
        Is(!(await Execute(new RideWaiting(db, config, new Actor(admin), coordinator), "accept", action)).Success, "non-participant cannot approve");
        Ok(await Execute(passengerWaiting, "reject", action));
        Is(ride.WaitingFee == 0, "rejection has no charge");
        Ok(await Execute(passengerWaiting, "add", request with { RequestKey = Guid.NewGuid() }));
        session = await db.RideWaitingSessions.SingleAsync(x => x.RideId == ride.Id && x.IsOpen);
        session.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3);
        await db.SaveChangesAsync();
        Ok(await Execute(passengerWaiting, "add", request with { RequestKey = Guid.NewGuid() }));
        Is(await db.RideWaitingSessions.CountAsync(x => x.RideId == ride.Id && x.IsOpen) == 1, "expired pending stop can be replaced atomically");
        session = await db.RideWaitingSessions.SingleAsync(x => x.RideId == ride.Id && x.IsOpen);
        action = action with { SessionId = session.Id };
        Ok(await Execute(driverWaiting, "accept", action));
        Ok(await Execute(driverWaiting, "accept", action));
        session.StartedAtUtc = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        var cash = new DriverCashPayment(db, config, new Actor(driver), accounting);
        Is(!(await Execute(cash, "add", new DriverCashPaymentModel(ride.Id, 1250, "YER", null, "wait-open"))).Success, "payment cannot race an open wait");
        Is(!(await Execute(new RideService(db, config, new Actor(driver), accounting), "update", new RideModel(Id: ride.Id, CustomerPaymentEnabled: true))).Success, "payment enable requires stopping wait");
        Ok(await Execute(driverWaiting, "update", action));
        Ok(await Execute(driverWaiting, "update", action));
        Is(ride.WaitingFee == 150 && ride.TotalAmount == 1250 && ride.DriverShare == 1035, "five minutes includes two free; fee and financial totals recorded once");
        Ok(await Execute(cash, "add", new DriverCashPaymentModel(ride.Id, 1250, "YER", null, "wait-settlement")));
        Is(await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id && x.Amount == 1250 && x.Status == PaymentStatus.Paid), "collected amount includes waiting");
        Is(!(await Execute(driverWaiting, "add", request with { RequestKey = Guid.NewGuid() })).Success, "no extra waiting after settlement");

        await AutomaticEnd(db, config, coordinator, customer, driver, kind, catalog, "connection_lost");
        await AutomaticEnd(db, config, coordinator, customer, driver, kind, catalog, "fee_cap");
        await AutomaticEnd(db, config, coordinator, customer, driver, kind, catalog, "vehicle_moving");
        Console.WriteLine("PASS: pricing boundaries, trusted quote, policy snapshot, consent, idempotency, waiting cutoff and balanced collection.");
    }

    private static async Task AutomaticEnd(YemenDriveDbContext db, DatabaseConfigurationStore config, RideWaitingCoordinator coordinator, int customer, int driver, int kind, int catalog, string reason)
    {
        var now = DateTime.UtcNow;
        var ride = new RideEntity { CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = catalog,
            CustomerPrice = 1000, Status = RideStatus.InProgress, WaitingFreeMinutes = 2, WaitingPricePerMinute = 50, WaitingMaxFee = reason == "fee_cap" ? 100 : 500 };
        db.Rides.Add(ride);
        await db.SaveChangesAsync();
        var session = new RideWaitingSession { RideId = ride.Id, RequestedByUserId = driver, ApprovedByUserId = customer, RequestKey = Guid.NewGuid(),
            StartedAtUtc = now.AddMinutes(-5), FreeMinutes = 2, PricePerMinute = 50, MaxFee = ride.WaitingMaxFee, Latitude = 15.36, Longitude = 44.20 };
        db.RideWaitingSessions.Add(session);
        await SetLocation(db, driver, reason == "connection_lost" ? now.AddMinutes(-3) : now);
        await db.SaveChangesAsync();
        var service = new RideWaiting(db, config, new Actor(customer), coordinator);
        if (reason == "vehicle_moving")
        {
            var location = await db.DriverLiveLocations.SingleAsync(x => x.DriverId == driver);
            location.Latitude += .005; location.Speed = 4;
            await db.SaveChangesAsync();
            Ok(await Execute(service, "get", new RideWaitingModel(ride.Id)));
            Is(session.IsOpen, "one GPS sample cannot stop waiting");
            session.MotionStartedAtUtc = DateTime.UtcNow.AddSeconds(-11);
            location.ObservedAtUtc = DateTime.UtcNow.AddMilliseconds(1);
            await db.SaveChangesAsync();
        }
        Ok(await Execute(service, "get", new RideWaitingModel(ride.Id)));
        Is(!session.IsOpen && session.EndReason == reason, "automatic waiting stop: " + reason);
        if (reason != "vehicle_moving") Is(ride.WaitingFee == 100, "cap or disconnect uses effective cutoff time");
        Ok(await Execute(service, "get", new RideWaitingModel(ride.Id)));
        Is(ride.WaitingFee == session.Fee, "reconciliation does not charge twice");
    }

    private static async Task SetLocation(YemenDriveDbContext db, int driver, DateTime time)
    {
        var location = await db.DriverLiveLocations.SingleOrDefaultAsync(x => x.DriverId == driver);
        if (location is null) { location = new DriverLiveLocation { DriverId = driver }; db.DriverLiveLocations.Add(location); }
        location.Latitude = 15.36; location.Longitude = 44.20; location.Speed = 0; location.IsOnline = true; location.ObservedAtUtc = time;
    }

    private static Task<ApiResult> Execute<T>(ModelService<T> service, string operation, T model) where T : class => service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    private static void Ok(ApiResult result) => Is(result.Success, result.Code + ": " + result.Message);
    private static void Is(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
    private sealed class RouteStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"routes\":[{\"distanceMeters\":3000,\"duration\":\"600s\",\"polyline\":{\"encodedPolyline\":\"test\"}}]}") });
    }
}
