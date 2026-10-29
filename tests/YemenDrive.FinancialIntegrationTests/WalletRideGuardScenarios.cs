using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Maps;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideService = YemenDrive.Application.Rides.Ride;
using CancelService = YemenDrive.Application.Rides.RideCancellationRequest;

internal static class WalletRideGuardScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config,
        int customer, int driver, int admin, int kind, int catalog)
    {
        var ride = new YemenDrive.Database.Entities.Ride
        {
            CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = catalog,
            Status = RideStatus.InProgress, StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CustomerPrice = 1000, ServiceFee = 100, TotalAmount = 1100,
            DriverCommissionAmount = 100, DriverShare = 900, PlatformShare = 200,
            WaitingPricePerMinute = 10, WaitingMaxFee = 500, HasRoutePricingSnapshot = true,
            DestinationLatitude = 15.2, DestinationLongitude = 44.2,
            PickupAddress = "اختبار", DestinationAddress = "الوجهة الأصلية"
        };
        var method = new PaymentMethod
        {
            Code = "guard-floosak", NameAr = "فلوسك الاختبارات", ProviderCode = "Floosak",
            Kind = PaymentMethodKind.ExternalWallet, IsActive = true, IsAvailableForRidePayment = true
        };
        db.Rides.Add(ride); db.PaymentMethods.Add(method); await db.SaveChangesAsync();
        var operation = new PaymentWalletOperation
        {
            RideId = ride.Id, CustomerId = customer, PaymentMethodId = method.Id,
            RequestId = $"guard-{Guid.NewGuid():N}", Amount = 1100, Mobile = "967711111111",
            Status = "ReviewRequired", IsOpen = true
        };
        db.PaymentWalletOperations.Add(operation); await db.SaveChangesAsync();
        var accounting = new RideAccountingPostingService(db, new AccountingPostingService(db));
        var driverRide = new RideService(db, config, new Actor(driver), accounting);
        var passengerCancel = new CancelService(db, config, new Actor(customer), accounting);
        var driverCancel = new CancelService(db, config, new Actor(driver), accounting);
        var adminCancel = new CancelService(db, config, new Actor(admin), accounting);
        var waiting = new RideWaiting(db, config, new Actor(customer), new RideWaitingCoordinator(db));
        var mapsHandler = new NoMapRequests();
        var maps = new GoogleMapsGateway(new HttpClient(mapsHandler),
            new GoogleMapsOptions { RoutesApiKey = "test-only" }, NullLogger<GoogleMapsGateway>.Instance);
        var routes = new RideRouteChangeService(db, config, new Actor(customer), maps);

        Code(await Call(waiting, "add", new RideWaitingModel(ride.Id, RequestKey: Guid.NewGuid())),
            "payment_already_open", "uncertain wallet operation prevents new waiting charges");
        Code(await Call(routes, "quote", new RideRouteChangeModel(ride.Id, RequestKey: Guid.NewGuid(),
            NewDestinationLatitude: 15.3, NewDestinationLongitude: 44.3, NewDestinationLabel: "وجهة جديدة")),
            "payment_already_open", "uncertain wallet operation freezes fare before route lookup");
        Check(mapsHandler.Requests == 0, "blocked repricing does not contact maps");
        foreach (var enabled in new[] { true, false })
            Code(await Call(driverRide, "update", new RideModel(Id: ride.Id, CustomerPaymentEnabled: enabled)),
                "wallet_payment_in_progress", "driver cannot alter payment controls during wallet settlement");
        Code(await Call(passengerCancel, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: "سبب اختبار")),
            "wallet_payment_in_progress", "rider cannot cancel an uncertain wallet debit");
        Code(await Call(driverCancel, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: "سبب اختبار")),
            "wallet_payment_in_progress", "driver cannot cancel an uncertain wallet debit");

        var request = new YemenDrive.Database.Entities.RideCancellationRequest
        {
            RideId = ride.Id, CustomerId = customer, DriverId = driver, RideStatusAtRequest = RideStatus.InProgress,
            Status = RideCancellationStatus.CustomerReviewPending, Reason = "طلب سابق"
        };
        db.RideCancellationRequests.Add(request); await db.SaveChangesAsync();
        Code(await Call(passengerCancel, "accept", new RideCancellationRequestModel(Id: request.Id)),
            "wallet_payment_in_progress", "stale rider cancellation approval cannot race wallet settlement");
        request.Status = RideCancellationStatus.DriverReviewPending; await db.SaveChangesAsync();
        Code(await Call(driverCancel, "accept", new RideCancellationRequestModel(Id: request.Id)),
            "wallet_payment_in_progress", "stale driver cancellation approval cannot race wallet settlement");
        request.Status = RideCancellationStatus.AdminReviewPending; await db.SaveChangesAsync();
        Code(await Call(adminCancel, "adminApprove", new RideCancellationRequestModel(Id: request.Id)),
            "wallet_payment_in_progress", "admin cannot finalize cancellation before provider result is known");
        Check(ride.Status == RideStatus.InProgress && ride.TotalAmount == 1100 &&
            !await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id) &&
            !await db.RideWaitingSessions.AnyAsync(x => x.RideId == ride.Id),
            "rejected actions leave fare, ride and money unchanged");
        var detail = Data(await Call(driverRide, "get", new RideModel(Id: ride.Id)));
        Check(detail.GetProperty("walletPaymentPending").GetBoolean() && !detail.GetProperty("paymentCompleted").GetBoolean(),
            "driver receives a pending status without a false paid receipt");

        request.Status = RideCancellationStatus.AdminRejected;
        operation.Status = "Succeeded";
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            RideId = ride.Id, UserId = customer, Amount = 1100, Currency = "YER", Provider = "Floosak",
            Status = PaymentStatus.Paid, ProviderReference = $"guard-paid-{ride.Id}"
        });
        await db.SaveChangesAsync();
        Code(await Call(driverRide, "update", new RideModel(Id: ride.Id, CustomerPaymentEnabled: false)),
            "ride_already_paid", "paid wallet trip cannot re-open payment controls");
        Check((await Call(driverRide, "update", new RideModel(Id: ride.Id, Status: RideStatus.Completed))).Success,
            "confirmed payment still allows normal driver completion");
        detail = Data(await Call(driverRide, "get", new RideModel(Id: ride.Id)));
        Check(!detail.GetProperty("walletPaymentPending").GetBoolean() && detail.GetProperty("paymentCompleted").GetBoolean(),
            "driver sees final paid status after settlement");
        Console.WriteLine("PASS: uncertain wallet operations freeze fare, payment controls and cancellation; confirmed payment allows driver completion.");
    }

    private static Task<ApiResult> Call<T>(ModelService<T> service, string operation, T model) where T : class =>
        service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    private static JsonElement Data(ApiResult result)
    {
        Check(result.Success, result.Code + ": " + result.Message);
        return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    private static void Code(ApiResult result, string code, string message) =>
        Check(!result.Success && result.Code == code, $"{message}; received {result.Code}: {result.Message}");
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
    private sealed class NoMapRequests : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; throw new InvalidOperationException("Maps must not be called while wallet payment reserves the ride."); }
    }
}
