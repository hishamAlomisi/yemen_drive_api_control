using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Drivers;
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

internal static class RepeatOfferAcceptanceScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int kind, int catalog)
    {
        foreach (var compatibility in new[] { true, false })
        {
            var suffix = compatibility ? "31" : "32";
            var customer = new User { PhoneNumber = "+9677008810" + suffix, DisplayName = "Repeat offer passenger", Role = UserRole.Customer };
            var driver = new User { PhoneNumber = "+9677008820" + suffix, DisplayName = "Repeat offer driver", Role = UserRole.Driver };
            db.Users.AddRange(customer, driver);
            await db.SaveChangesAsync();
            db.DriverProfiles.Add(new DriverProfile { UserId = driver.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog,
                IsAvailable = true, AcceptsNewRequests = true, IsApproved = true, ApprovedPassengerSeats = 4, PlateNumber = "REPEAT-" + suffix });
            var location = new DriverLiveLocation { DriverId = driver.Id, Latitude = 15, Longitude = 44, IsOnline = true, ObservedAtUtc = DateTime.UtcNow };
            db.DriverLiveLocations.Add(location);
            var previous = new RideEntity { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog,
                Status = RideStatus.InProgress, StartedAtUtc = DateTime.UtcNow.AddMinutes(-10), ServiceEndedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                PickupLatitude = 15, PickupLongitude = 44, DestinationLatitude = 15.01, DestinationLongitude = 44.01,
                PickupAddress = "previous pickup", DestinationAddress = "previous destination", CustomerPrice = 1000, TotalAmount = 1000, PaymentMethodCode = "cash" };
            db.Rides.Add(previous);
            await db.SaveChangesAsync();
            // Persisted paid history with an older operational status is a legal
            // legacy shape. Its driver relationship must remain historical.
            db.PaymentTransactions.Add(new PaymentTransaction { UserId = customer.Id, RideId = previous.Id, Amount = 1000,
                Provider = "Cash", Status = PaymentStatus.Paid, Currency = "YER", DriverNetAmount = 1000, ServiceFeeAmount = 0 });
            var quote = new RidePricingQuote { CustomerId = customer.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog,
                PickupLatitude = 15, PickupLongitude = 44, DestinationLatitude = 15.01, DestinationLongitude = 44.01,
                Amount = 1839, MinPrice = 1600, MaxPrice = 2000, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
            db.RidePricingQuotes.Add(quote);
            await db.SaveChangesAsync();
            var draft = new RideModel(ServiceKindId: kind, ServiceCatalogItemId: catalog, PickupAddress: "new pickup", DestinationAddress: "new destination",
                PickupLatitude: 15, PickupLongitude: 44, DestinationLatitude: 15.01, DestinationLongitude: 44.01,
                CustomerPrice: 1839, PricingQuoteToken: quote.Token, IdempotencyKey: "repeat-new-" + suffix);
            var rides = new RideService(db, config, new Actor(customer.Id), new RideAccountingPostingService(db, new AccountingPostingService(db)));
            var nextId = Data(await Call(rides, "add", draft)).GetProperty("id").GetInt32();
            Check(nextId != previous.Id, "new request after service end has a new ride identity");
            Check(Data(await Call(rides, "add", draft)).GetProperty("id").GetInt32() == nextId, "retrying new request keeps its own idempotency identity");
            var offers = new OfferService(db, config, new Actor(driver.Id));
            var offerId = Data(await Call(offers, "add", new RideOfferModel(RideId: nextId, Amount: 1839))).GetProperty("id").GetInt32();
            Task<ApiResult> Accept(int? rideId = null) => compatibility
                ? Call(new RideOfferAction(db, config, new Actor(customer.Id)), "accept", new RideOfferActionModel(rideId ?? nextId, offerId))
                : Call(new OfferService(db, config, new Actor(customer.Id)), "accept", new RideOfferModel(Id: offerId));
            async Task RejectWith(string code)
            {
                var result = await Accept();
                Check(!result.Success && result.Code == code, $"{compatibility}: expected {code}, got {result.Code}");
                Check(await db.RideOffers.AsNoTracking().AnyAsync(x => x.Id == offerId && x.Status == OfferStatus.Pending), "rejection leaves offer pending");
                Check(await db.Rides.AsNoTracking().AnyAsync(x => x.Id == nextId && x.DriverId == null), "rejection does not assign new ride");
            }
            if (compatibility)
            {
                var wrongRide = await Accept(previous.Id);
                Check(!wrongRide.Success && wrongRide.Code == "offer_not_found", "offer cannot be accepted against the previous ride id");
            }
            previous.Status = RideStatus.Completed;
            previous.CompletedAtUtc = previous.ServiceEndedAtUtc;
            await db.SaveChangesAsync();
            await RejectWith("rating_required");
            Data(await Call(new YemenDrive.Application.Drivers.DriverRating(db, config, new Actor(customer.Id)), "add", new DriverRatingModel(RideId: previous.Id, Score: 5)));

            // Financial cases are independently guarded, including when they
            // appear after the request/offer was created. Never waive that debt.
            previous.TotalAmount = 1100;
            var debt = new RideFinancialCase { RideId = previous.Id, CustomerId = customer.Id, DriverId = driver.Id, Amount = 100,
                Status = "Open", SourceReference = "repeat-offer-debt-" + suffix };
            db.RideFinancialCases.Add(debt);
            await db.SaveChangesAsync();
            await RejectWith("outstanding_payment_required");
            Check(await db.RideFinancialCases.AsNoTracking().AnyAsync(x => x.Id == debt.Id && x.Amount == 100 && x.Status == "Open"), "acceptance does not clear a receivable");
            debt.Status = "Confirmed"; await db.SaveChangesAsync();
            await RejectWith("outstanding_payment_required");
            // A documented dispute remains an independent case. It neither
            // reserves the driver nor turns a pending amount into a receipt.
            debt.Status = "Disputed";
            previous.Status = RideStatus.InProgress; previous.ServiceEndedAtUtc = null;
            await db.SaveChangesAsync();
            await RejectWith("driver_not_available");
            previous.ServiceEndedAtUtc = DateTime.UtcNow.AddMinutes(-1);
            location.ObservedAtUtc = DateTime.UtcNow.AddMinutes(-6);
            await db.SaveChangesAsync();
            await RejectWith("driver_not_available");
            location.ObservedAtUtc = DateTime.UtcNow;
            if (compatibility)
            {
                // The reported client scenario: the earlier ride is completed,
                // rated and fully paid before accepting another driver's offer.
                previous.Status = RideStatus.Completed;
                previous.TotalAmount = 1000;
                debt.Amount = 0;
                debt.Status = "Resolved";
            }
            await db.SaveChangesAsync();
            var debtAmountBeforeAccept = debt.Amount;
            var debtStatusBeforeAccept = debt.Status;
            Data(await Accept());
            Check(await db.Rides.AsNoTracking().AnyAsync(x => x.Id == nextId && x.DriverId == driver.Id && x.Status == RideStatus.DriverAssigned), "same driver accepts next ride after previous service ended");
            Check(await db.RideOffers.AsNoTracking().AnyAsync(x => x.Id == offerId && x.Status == OfferStatus.Accepted), "exact pending offer is accepted");
            Check(await db.RideFinancialCases.AsNoTracking().AnyAsync(x => x.Id == debt.Id && x.Amount == debtAmountBeforeAccept && x.Status == debtStatusBeforeAccept), "acceptance preserves previous financial case");
            Check(await db.PaymentTransactions.CountAsync(x => x.RideId == previous.Id) == 1 && !await db.PaymentTransactions.AnyAsync(x => x.RideId == nextId), "offer acceptance does not collect money or duplicate historical receipts");
            var replay = await Accept();
            Check(!replay.Success && replay.Code is "ride_not_open_for_offers" or "offer_not_pending", "a duplicate acceptance remains a precise conflict, with no duplicate assignment");
            Console.WriteLine($"PASS: {(compatibility ? "Flutter compatibility" : "general")} repeat-offer acceptance preserves identity, rating, debt, service-end and fresh-location guards.");
        }
    }

    private static Task<ApiResult> Call<T>(ModelService<T> service, string operation, T model) where T : class =>
        service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    private static JsonElement Data(ApiResult result)
    {
        Check(result.Success, result.Code + ": " + result.Message);
        return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
}
