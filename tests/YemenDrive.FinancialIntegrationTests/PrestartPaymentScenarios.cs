using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Payments;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;
using RideService = YemenDrive.Application.Rides.Ride;
using OfferService = YemenDrive.Application.Rides.RideOffer;
using CancelService = YemenDrive.Application.Rides.RideCancellationRequest;

internal static class PrestartPaymentScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int admin, int kind, int catalog)
    {
        var customer = new User { PhoneNumber = "+967700123450", DisplayName = "Prepayment rider", Role = UserRole.Customer };
        var method = new YemenDrive.Database.Entities.PaymentMethod { Code = "prestart-floosak", NameAr = "فلوسك للاختبار المبكر", ProviderCode = "Floosak",
            IsActive = true, IsAvailableForRidePayment = true, Kind = PaymentMethodKind.ExternalWallet };
        db.AddRange(customer, method); await db.SaveChangesAsync();
        var wallet = new Wallet { UserId = customer.Id, Currency = "YER", Balance = 100000 };
        db.Add(wallet); await db.SaveChangesAsync();
        var accounting = new RideAccountingPostingService(db, new AccountingPostingService(db));
        var riderRides = new RideService(db, config, new Actor(customer.Id), accounting);
        var payments = new Payment(db, config, new Actor(customer.Id), accounting);

        async Task<RideEntity> Book(string choice, bool compatibility)
        {
            var driver = new User { PhoneNumber = "+967" + Random.Shared.Next(710000000, 719999999), Role = UserRole.Driver };
            db.Add(driver); await db.SaveChangesAsync();
            db.AddRange(new Wallet { UserId = driver.Id, Currency = "YER" },
                new DriverProfile { UserId = driver.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog, IsAvailable = true },
                new DriverLiveLocation { DriverId = driver.Id, Latitude = 15, Longitude = 44, IsOnline = true, ObservedAtUtc = DateTime.UtcNow });
            var quote = new RidePricingQuote { CustomerId = customer.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog,
                PickupLatitude = 15, PickupLongitude = 44, DestinationLatitude = 15.01, DestinationLongitude = 44.01,
                Amount = 1000, MinPrice = 800, MaxPrice = 1200, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
            db.Add(quote); await db.SaveChangesAsync();
            var created = Data(await Call(riderRides, "add", new RideModel(ServiceKindId: kind, ServiceCatalogItemId: catalog,
                PickupAddress: "pickup", DestinationAddress: "destination", PickupLatitude: 15, PickupLongitude: 44,
                DestinationLatitude: 15.01, DestinationLongitude: 44.01, CustomerPrice: 1000,
                PricingQuoteToken: quote.Token, PaymentMethodCode: choice)));
            var ride = await db.Rides.SingleAsync(x => x.Id == created.GetProperty("id").GetInt32());
            Check(ride.PaymentMethodCode == choice && !ride.PaymentRequiredBeforeStart, "booking persists choice without charging before acceptance");
            var offer = new YemenDrive.Database.Entities.RideOffer { RideId = ride.Id, DriverId = driver.Id, Amount = 1000,
                Status = OfferStatus.Pending, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
            db.Add(offer); await db.SaveChangesAsync();
            var accepted = compatibility
                ? await Call(new RideOfferAction(db, config, new Actor(customer.Id)), "accept", new RideOfferActionModel(ride.Id, offer.Id))
                : await Call(new OfferService(db, config, new Actor(customer.Id)), "accept", new RideOfferModel(Id: offer.Id));
            var result = Data(accepted);
            Check(result.GetProperty("paymentRequiredBeforeStart").GetBoolean() == (choice != "cash") &&
                ride.PaymentRequiredBeforeStart == (choice != "cash"), "both offer acceptance APIs establish prepayment requirement");
            await new FinancialAccountProvisioningService(db).ProvisionExistingAsync(CancellationToken.None);
            return ride;
        }

        var trip = await Book("wallet", true);
        var driverRides = new RideService(db, config, new Actor(trip.DriverId!.Value), accounting);
        Data(await Call(driverRides, "update", new RideModel(Id: trip.Id, Status: RideStatus.DriverEnRoute)));
        Data(await Call(driverRides, "update", new RideModel(Id: trip.Id, Status: RideStatus.DriverArrived)));
        Code(await Call(driverRides, "update", new RideModel(Id: trip.Id, Status: RideStatus.InProgress)), "payment_required_before_start");
        Code(await Call(driverRides, "update", new RideModel(Id: trip.Id, PaymentMethodCode: "cash")), "ride_access_denied");
        Code(await Call(payments, "add", new PaymentModel(RideId: trip.Id, Amount: trip.TotalAmount!.Value - 1)), "invalid_payment_amount");
        var beforeBalance = wallet.Balance;
        var paid = Data(await Call(payments, "add", new PaymentModel(RideId: trip.Id, Amount: trip.TotalAmount!.Value,
            IdempotencyKey: "prestart-internal")));
        Data(await Call(payments, "add", new PaymentModel(RideId: trip.Id, Amount: trip.TotalAmount!.Value,
            IdempotencyKey: "prestart-internal")));
        Check(wallet.Balance == beforeBalance - trip.TotalAmount && !trip.PaymentRequiredBeforeStart &&
            await db.PaymentTransactions.CountAsync(x => x.RideId == trip.Id && x.Status == PaymentStatus.Paid) == 1,
            "internal early settlement debits exactly once and clears requirement atomically");
        Code(await Call(riderRides, "update", new RideModel(Id: trip.Id, PaymentMethodCode: "cash")), "ride_already_paid");
        Data(await Call(driverRides, "update", new RideModel(Id: trip.Id, Status: RideStatus.InProgress)));
        var detail = Data(await Call(riderRides, "get", new RideModel(Id: trip.Id)));
        Check(detail.GetProperty("paymentCompleted").GetBoolean() && detail.GetProperty("paymentProvider").GetString() == "YemenDriveWallet" &&
            detail.GetProperty("paymentMethodNameAr").GetString() == "محفظة يمن درايف", "internal paid metadata comes from settled payment");
        Check(trip.StartedAtUtc != null && trip.WaitingFee == 0, "prepaid trip starts without changing settled amount");

        var cashTrip = await Book("wallet", true);
        var reservation = new PaymentWalletOperation { RideId = cashTrip.Id, CustomerId = customer.Id, PaymentMethodId = method.Id,
            RequestId = "prestart-unknown", Amount = cashTrip.TotalAmount!.Value, Status = "ReviewRequired", IsOpen = true };
        db.Add(reservation); await db.SaveChangesAsync();
        Code(await Call(riderRides, "update", new RideModel(Id: cashTrip.Id, PaymentMethodCode: "cash")), "wallet_payment_in_progress");
        Check(cashTrip.PaymentRequiredBeforeStart && cashTrip.PaymentMethodCode == "wallet", "unknown provider outcome never enables cash fallback");
        reservation.Status = "Cancelled"; reservation.IsOpen = false; await db.SaveChangesAsync();
        var switched = Data(await Call(riderRides, "update", new RideModel(Id: cashTrip.Id, PaymentMethodCode: "cash")));
        Check(!switched.GetProperty("paymentRequiredBeforeStart").GetBoolean() && cashTrip.PaymentMethodCode == "cash" &&
            !await db.PaymentTransactions.AnyAsync(x => x.RideId == cashTrip.Id) &&
            !await db.CashPaymentRequests.AnyAsync(x => x.RideId == cashTrip.Id) &&
            !await db.CashCollectionApprovals.AnyAsync(x => x.RideId == cashTrip.Id), "explicit cash switch changes only choice, never records collection");
        driverRides = new RideService(db, config, new Actor(cashTrip.DriverId!.Value), accounting);
        Data(await Call(driverRides, "update", new RideModel(Id: cashTrip.Id, Status: RideStatus.DriverArrived)));
        Data(await Call(driverRides, "update", new RideModel(Id: cashTrip.Id, Status: RideStatus.InProgress)));

        var racedTrip = await Book("wallet", false);
        var isolatedOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(db.Database.GetConnectionString()).Options;
        async Task<ApiResult> Race(bool switchToCash)
        {
            await using var isolated = new YemenDriveDbContext(isolatedOptions);
            var isolatedAccounting = new RideAccountingPostingService(isolated, new AccountingPostingService(isolated));
            return switchToCash
                ? await Call(new RideService(isolated, config, new Actor(customer.Id), isolatedAccounting), "update",
                    new RideModel(Id: racedTrip.Id, PaymentMethodCode: "cash"))
                : await Call(new Payment(isolated, config, new Actor(customer.Id), isolatedAccounting), "add",
                    new PaymentModel(RideId: racedTrip.Id, Amount: racedTrip.TotalAmount!.Value, IdempotencyKey: "prestart-race"));
        }
        var race = await Task.WhenAll(Race(true), Race(false));
        Check(race.Count(x => x.Success) == 1, "cash switch and early debit cannot both win concurrently");
        await db.Entry(racedTrip).ReloadAsync();
        var racedPaidCount = await db.PaymentTransactions.CountAsync(x => x.RideId == racedTrip.Id && x.Status == PaymentStatus.Paid);
        Check(!racedTrip.PaymentRequiredBeforeStart && (racedTrip.PaymentMethodCode == "cash" ? racedPaidCount == 0 : racedPaidCount == 1),
            "cash/debit race leaves one authoritative tender and no false paid cash choice");

        var externalTrip = await Book(method.Code, false);
        var operation = new PaymentWalletOperation { RideId = externalTrip.Id, CustomerId = customer.Id, PaymentMethodId = method.Id,
            ProviderCode = "Floosak", RequestId = "prestart-success", Amount = externalTrip.TotalAmount!.Value,
            Status = "ProviderSucceeded", ProviderSucceededAtUtc = DateTime.UtcNow, IsOpen = true };
        db.Add(operation); await db.SaveChangesAsync();
        var settlement = new ExternalWalletPaymentSettlement(db, accounting);
        await settlement.SettleAsync(operation, CancellationToken.None);
        await settlement.SettleAsync(operation, CancellationToken.None);
        detail = Data(await Call(riderRides, "get", new RideModel(Id: externalTrip.Id)));
        Check(!externalTrip.PaymentRequiredBeforeStart && externalTrip.Status == RideStatus.DriverAssigned &&
            detail.GetProperty("paymentProvider").GetString() == "Floosak" &&
            detail.GetProperty("paymentMethodNameAr").GetString() == method.NameAr &&
            await db.PaymentTransactions.CountAsync(x => x.RideId == externalTrip.Id && x.Status == PaymentStatus.Paid) == 1,
            "external receipt settles before movement once and exposes actual method metadata");
        var report = new RideReport(db, config, new Actor(customer.Id));
        var listed = Data(await Call(report, "list", new RideModel())).EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == externalTrip.Id);
        Check(listed.GetProperty("paymentCompleted").GetBoolean() && listed.GetProperty("paymentMethodNameAr").GetString() == method.NameAr,
            "list metadata agrees with detail after early settlement");
        var driverCancel = new CancelService(db, config, new Actor(externalTrip.DriverId!.Value), accounting);
        var cancellation = Data(await Call(driverCancel, "add", new RideCancellationRequestModel(RideId: externalTrip.Id, Reason: "تعذر بدء الرحلة")));
        var requestId = cancellation.GetProperty("id").GetInt32();
        Check(externalTrip.Status == RideStatus.CancellationPending &&
            (await db.RideCancellationRequests.SingleAsync(x => x.Id == requestId)).Status == RideCancellationStatus.AdminReviewPending,
            "driver cannot silently discard paid prestart ride; existing financial review is required");
        Code(await Call(new CancelService(db, config, new Actor(admin), accounting), "adminApprove", new RideCancellationRequestModel(Id: requestId)),
            "manual_refund_required");
        db.ChangeTracker.Clear();
        Check(!await db.PaymentTransactions.AnyAsync(x => x.RideId == externalTrip.Id && x.Status == PaymentStatus.Refunded) &&
            (await db.RideCancellationRequests.SingleAsync(x => x.Id == requestId)).Status == RideCancellationStatus.AdminReviewPending,
            "manual external refund failure creates no refunded funds or approved cancellation");
        Console.WriteLine("PASS: booking payment choice, both acceptance APIs, prestart gate, internal/external settlement, explicit cash fallback, metadata and paid cancellation safeguards.");
    }

    private static Task<ApiResult> Call<T>(ModelService<T> service, string operation, T model) where T : class =>
        service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    private static JsonElement Data(ApiResult result)
    {
        Check(result.Success, result.Code + ": " + result.Message);
        return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    private static void Code(ApiResult result, string code) => Check(!result.Success && result.Code == code,
        $"Expected {code}, received {result.Code}: {result.Message}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
}
