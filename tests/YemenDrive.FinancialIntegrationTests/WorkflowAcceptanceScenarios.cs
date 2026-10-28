using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Api;
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
using CancelService = YemenDrive.Application.Rides.RideCancellationRequest;
using RatingService = YemenDrive.Application.Drivers.DriverRating;

internal static class WorkflowAcceptanceScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int admin, int kind, int catalog)
    {
        var customer = new User { PhoneNumber = "+967711111101", DisplayName = "Workflow passenger", PasswordHash = "test", Role = UserRole.Customer };
        var driver = new User { PhoneNumber = "+967711111102", DisplayName = "Workflow driver", PasswordHash = "test", Role = UserRole.Driver };
        db.Users.AddRange(customer, driver); await db.SaveChangesAsync();
        var profile = new DriverProfile { UserId = driver.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog, VehicleModel = "test", PlateNumber = "WF-1", IsAvailable = true };
        var location = new DriverLiveLocation { DriverId = driver.Id, Latitude = 15, Longitude = 44, IsOnline = true, ObservedAtUtc = DateTime.UtcNow };
        db.DriverProfiles.Add(profile); db.DriverLiveLocations.Add(location); await db.SaveChangesAsync();
        var accounting = new RideAccountingPostingService(db, new AccountingPostingService(db));
        var passenger = new CancelService(db, config, new Actor(customer.Id), accounting);
        var chauffeur = new CancelService(db, config, new Actor(driver.Id), accounting);
        var administration = new CancelService(db, config, new Actor(admin), accounting);
        var driverRides = new RideService(db, config, new Actor(driver.Id), accounting);
        var passengerRides = new RideService(db, config, new Actor(customer.Id), accounting);

        async Task<RideEntity> Trip(RideStatus status)
        {
            var trip = new RideEntity { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind,
                ServiceCatalogItemId = catalog, Status = status, CustomerPrice = 1000, TotalAmount = 1000,
                PickupLatitude = 15, PickupLongitude = 44, PickupAddress = "pickup", DestinationAddress = "destination",
                StartedAtUtc = status == RideStatus.InProgress ? DateTime.UtcNow.AddMinutes(-5) : null };
            db.Rides.Add(trip); profile.IsAvailable = false; await db.SaveChangesAsync(); return trip;
        }

        var trip = await Trip(RideStatus.InProgress);
        var requested = Data(await Call(chauffeur, "add", new RideCancellationRequestModel(RideId: trip.Id, Reason: "عطل مفاجئ")));
        var requestId = requested.GetProperty("id").GetInt32();
        Check(trip.Status == RideStatus.InProgress && !profile.IsAvailable, "driver request keeps trip running pending passenger consent");
        Check(!(await Call(chauffeur, "accept", new RideCancellationRequestModel(Id: requestId))).Success, "driver cannot approve own request");
        Ok(await Call(passenger, "reject", new RideCancellationRequestModel(Id: requestId)));
        Check(trip.Status == RideStatus.InProgress, "passenger refusal keeps same ride status");
        requestId = Data(await Call(chauffeur, "add", new RideCancellationRequestModel(RideId: trip.Id, Reason: "عطل مستمر"))).GetProperty("id").GetInt32();
        Ok(await Call(passenger, "accept", new RideCancellationRequestModel(Id: requestId)));
        Ok(await Call(passenger, "accept", new RideCancellationRequestModel(Id: requestId)));
        Check(trip.Status == RideStatus.Cancelled && profile.IsAvailable, "consent cancels exactly once and releases driver");

        trip = await Trip(RideStatus.DriverEnRoute);
        Check(!(await Call(driverRides, "update", new RideModel(Id: trip.Id, Status: RideStatus.InProgress))).Success, "arrival is required before trip start");
        Check(!(await Call(passengerRides, "update", new RideModel(Id: trip.Id, Status: RideStatus.DriverArrived))).Success, "passenger cannot confirm driver arrival");
        Ok(await Call(driverRides, "update", new RideModel(Id: trip.Id, Status: RideStatus.DriverArrived)));
        Check(trip.StartedAtUtc == null && trip.Status == RideStatus.DriverArrived, "arrival does not start the trip");
        Check(await new TrackingHubAccessPolicy(db).CanJoinRideAsync(customer.Id, trip.Id, CancellationToken.None), "arrived trip still supports live tracking");
        Check(await db.Notifications.AnyAsync(n => n.UserId == customer.Id && n.Body.Contains("وصل السائق")), "passenger notified of arrival");
        Ok(await Call(chauffeur, "add", new RideCancellationRequestModel(RideId: trip.Id, Reason: "تعذر الانطلاق")));
        Check(trip.Status == RideStatus.Cancelled && profile.IsAvailable, "driver cancels pre-start ride immediately");

        trip = await Trip(RideStatus.InProgress);
        requestId = Data(await Call(passenger, "add", new RideCancellationRequestModel(RideId: trip.Id, Reason: "تغيير الخطة"))).GetProperty("id").GetInt32();
        Ok(await Call(chauffeur, "accept", new RideCancellationRequestModel(Id: requestId)));
        Check(trip.Status == RideStatus.Cancelled && profile.IsAvailable, "driver approval releases driver from passenger cancellation");
        trip = await Trip(RideStatus.InProgress);
        requestId = Data(await Call(passenger, "add", new RideCancellationRequestModel(RideId: trip.Id, Reason: "طلب مراجعة"))).GetProperty("id").GetInt32();
        Ok(await Call(chauffeur, "refer", new RideCancellationRequestModel(Id: requestId)));
        Ok(await Call(administration, "adminApprove", new RideCancellationRequestModel(Id: requestId)));
        Check(trip.Status == RideStatus.Cancelled && profile.IsAvailable, "admin approval releases driver from passenger cancellation");

        trip = await Trip(RideStatus.Completed);
        profile.IsAvailable = true; await db.SaveChangesAsync();
        var draft = new RideModel(ServiceKindId: kind, ServiceCatalogItemId: catalog, PickupAddress: "pickup", DestinationAddress: "destination", PickupLatitude: 15, PickupLongitude: 44, DestinationLatitude: 15.01, DestinationLongitude: 44.01, CustomerPrice: 1000);
        var pending = new PendingRideRating(db, config, new Actor(customer.Id));
        Check(Data(await Call(pending, "get", new PendingRideRatingModel())).GetProperty("rideId").GetInt32() == trip.Id, "pending review is restored from server");
        Check((await Call(passengerRides, "add", draft)).Code == "rating_required", "server blocks new booking until rating");
        var rating = new RatingService(db, config, new Actor(customer.Id));
        Ok(await Call(rating, "add", new DriverRatingModel(RideId: trip.Id, Score: 5)));
        Ok(await Call(rating, "add", new DriverRatingModel(RideId: trip.Id, Score: 5)));
        Check((await Call(pending, "get", new PendingRideRatingModel())).Data == null, "review not requested twice");
        var quote = new RidePricingQuote { CustomerId = customer.Id, ServiceKindId = kind, ServiceCatalogItemId = catalog,
            PickupLatitude = 15, PickupLongitude = 44, DestinationLatitude = 15.01, DestinationLongitude = 44.01,
            Amount = 1000, MinPrice = 800, MaxPrice = 1200, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
        db.RidePricingQuotes.Add(quote); await db.SaveChangesAsync();
        var search = Data(await Call(passengerRides, "add", draft with { PricingQuoteToken = quote.Token }));
        var searchId = search.GetProperty("id").GetInt32();
        var recipients = new RideSearchRecipients(db, config, new Actor(customer.Id));
        var sent = Data(await Call(recipients, "get", new RideSearchRecipientModel(searchId)));
        Check(sent.EnumerateArray().Any(x => x.GetProperty("driverId").GetInt32() == driver.Id), "persisted dispatched driver appears to passenger");
        Check(!(await Call(new RideSearchRecipients(db, config, new Actor(driver.Id)), "get", new RideSearchRecipientModel(searchId))).Success, "recipient list private to passenger");
        var invitation = await db.Notifications.Where(n => n.UserId == driver.Id && n.DataJson == $"{{\"action\":\"open_ride_offer\",\"rideId\":{searchId}}}").OrderByDescending(n => n.Id).FirstAsync();
        Check(await RideRequestNotificationPolicy.CanAlertAsync(db, invitation, CancellationToken.None), "fresh available request alerts online driver");
        invitation.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        Check(!await RideRequestNotificationPolicy.CanAlertAsync(db, invitation, CancellationToken.None), "backlogged requests do not alert");
        invitation.CreatedAtUtc = DateTime.UtcNow;
        profile.IsAvailable = false; await db.SaveChangesAsync();
        Check(!await RideRequestNotificationPolicy.CanAlertAsync(db, invitation, CancellationToken.None), "unavailable driver receives no new-request alert");
        profile.IsAvailable = true; location.IsOnline = false; await db.SaveChangesAsync();
        Check(!await RideRequestNotificationPolicy.CanAlertAsync(db, invitation, CancellationToken.None), "offline driver receives no new-request alert");
        location.IsOnline = true;
        var open = await db.Rides.SingleAsync(x => x.Id == searchId); open.Status = RideStatus.Cancelled; await db.SaveChangesAsync();
        Check(!await RideRequestNotificationPolicy.CanAlertAsync(db, invitation, CancellationToken.None), "cancelled requests never alert on reconnect");
        open.Status = RideStatus.DriverAssigned; open.DriverId = driver.Id; await db.SaveChangesAsync();
        Check(!await RideRequestNotificationPolicy.CanAlertAsync(db, invitation, CancellationToken.None), "assigned requests never alert as new");
        Console.WriteLine("PASS: workflow acceptance: driver arrival, consent/cancellation, availability, required rating, actual recipients and stale request notification policy.");
    }

    private static Task<ApiResult> Call<T>(ModelService<T> service, string operation, T model) where T : class =>
        service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    private static JsonElement Data(ApiResult result) { Ok(result); return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
    private static void Ok(ApiResult result) => Check(result.Success, result.Code + ": " + result.Message);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
}
