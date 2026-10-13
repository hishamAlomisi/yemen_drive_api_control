using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YemenDrive.Application.DriverPayments;
using YemenDrive.Application.DriverSettlements;
using YemenDrive.Application.Accounting;
using DriverSettlementPaymentService = YemenDrive.Application.DriverSettlements.DriverSettlementPayment;
using YemenDrive.Application.Payments;
using YemenDrive.Application.Rides;
using YemenDrive.Application.Safety;
using YemenDrive.Api;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideService = YemenDrive.Application.Rides.Ride;
using RideOfferService = YemenDrive.Application.Rides.RideOffer;
using RideCancellationRequestService = YemenDrive.Application.Rides.RideCancellationRequest;
using CashCollectionApprovalService = YemenDrive.Application.DriverPayments.CashCollectionApproval;
using CashPaymentRequestService = YemenDrive.Application.DriverPayments.CashPaymentRequest;
using UserService = YemenDrive.Application.Users.User;
using ServiceKindService = YemenDrive.Application.Catalog.ServiceKind;

var databaseName = $"YemenDrive_FinancialTests_{Guid.NewGuid():N}";
var options = new DbContextOptionsBuilder<YemenDriveDbContext>()
    .UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True")
    .Options;
var settingsPath = Path.Combine(Path.GetTempPath(), $"yemendrive-financial-tests-{Guid.NewGuid():N}.json");
var configuration = new DatabaseConfigurationStore(settingsPath);
await configuration.SaveAsync(new DatabaseSettings
{
    Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = databaseName,
    IntegratedSecurity = true, TrustServerCertificate = true
}, CancellationToken.None);

try
{
    await using var db = new YemenDriveDbContext(options);
    await db.Database.MigrateAsync();
    var fixture = await SeedAsync(db);

    await TrackingHubMembershipIsRestrictedToActiveRideParticipantsAsync(db, fixture);
    await TrackingHubRequiresAuthenticatedRideMembershipAsync(db, configuration, fixture, databaseName);
    await TrustedDeviceCredentialsRequireAValidUnexpiredServerTokenAsync(db, fixture);
    await ServerSessionsRotateAndRevokeCredentialsAsync(db, fixture);
    OtpChallengesAreConsoleOnlyInDevelopmentAndLimitedToFiveAttempts();
    await FinancialAccountsAreProvisionedForUsersAndServiceKindsAsync(db, configuration, fixture);
    await GeneralLedgerFoundationIsBalancedAndImmutableAsync(db, configuration, fixture);
    await WalletPaymentIsAtomicAndIdempotentAsync(db, configuration, fixture);
    await RideCancellationRequiresReasonAndFollowsReviewWorkflowAsync(db, configuration, fixture);
    await CashSettlementHandlesExcessAndShortageAsync(db, configuration, fixture);
    await CustomerCashPaymentRequiresDriverConfirmationAsync(db, configuration, fixture);
    await InsufficientCashShortageRollsBackAsync(db, configuration, fixture);
    await AdministrativeApprovalSettlesPaidCashCancellationAsync(db, configuration, fixture);
    await RideSearchAndOfferEventsArePublishedAfterPersistenceAsync(db, configuration, fixture);

    Console.WriteLine("PASS: financial integration scenarios completed on an isolated temporary database.");
}
finally
{
    await using var cleanup = new YemenDriveDbContext(options);
    await cleanup.Database.EnsureDeletedAsync();
    if (File.Exists(settingsPath)) File.Delete(settingsPath);
}

static async Task TrackingHubMembershipIsRestrictedToActiveRideParticipantsAsync(
    YemenDriveDbContext db,
    Fixture fixture)
{
    var policy = new TrackingHubAccessPolicy(db);
    var activeRide = await AddRideAsync(
        db, fixture, RideStatus.DriverAssigned, 900m, 100m, 0m);

    Assert(await policy.CanJoinRideAsync(fixture.CustomerId, activeRide.Id, CancellationToken.None),
        "the ride customer can join the private tracking group");
    Assert(await policy.CanJoinRideAsync(fixture.DriverId, activeRide.Id, CancellationToken.None),
        "the assigned driver can join the private tracking group");
    Assert(await policy.CanJoinRideAsync(fixture.AdminId, activeRide.Id, CancellationToken.None),
        "an active administrator can join the private tracking group");
    Assert(!await policy.CanJoinRideAsync(int.MaxValue, activeRide.Id, CancellationToken.None),
        "an unrelated account cannot join the private tracking group");
    Assert(await policy.IsActiveAdministratorAsync(fixture.AdminId, CancellationToken.None),
        "an active administrator can subscribe to operations");
    Assert(!await policy.IsActiveAdministratorAsync(fixture.CustomerId, CancellationToken.None),
        "a customer cannot subscribe to operations");

    var completedRide = await AddRideAsync(
        db, fixture, RideStatus.Completed, 900m, 100m, 0m);
    Assert(!await policy.CanJoinRideAsync(fixture.CustomerId, completedRide.Id, CancellationToken.None),
        "a completed ride no longer accepts tracking subscriptions");
}

static async Task RideSearchAndOfferEventsArePublishedAfterPersistenceAsync(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    Fixture fixture)
{
    const double latitude = 15.3694;
    const double longitude = 44.1910;
    var candidateDriver = new User
    {
        PhoneNumber = "+967700000004",
        DisplayName = "Realtime Test Driver",
        PasswordHash = "test",
        Role = UserRole.Driver
    };
    db.Users.Add(candidateDriver);
    await db.SaveChangesAsync();
    db.DriverProfiles.Add(new DriverProfile
    {
        UserId = candidateDriver.Id,
        ServiceKindId = fixture.ServiceKindId,
        ServiceCatalogItemId = fixture.ServiceCatalogItemId,
        VehicleModel = "Test vehicle",
        PlateNumber = "TEST-1",
        IsAvailable = true
    });
    db.DriverLiveLocations.Add(new DriverLiveLocation
    {
        DriverId = candidateDriver.Id,
        Latitude = latitude,
        Longitude = longitude,
        IsOnline = true,
        ObservedAtUtc = DateTime.UtcNow
    });
    await db.SaveChangesAsync();

    var publisher = new RecordingRideRealtimePublisher();
    var rideService = new RideService(
        db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db), publisher);
    var rideResult = await ExecuteAsync(rideService, "add", new RideModel(
        ServiceKindId: fixture.ServiceKindId,
        ServiceCatalogItemId: fixture.ServiceCatalogItemId,
        PickupLabel: "نقطة الاختبار",
        PickupAddress: "نقطة الاختبار",
        PickupLatitude: latitude,
        PickupLongitude: longitude,
        DestinationLabel: "وجهة الاختبار",
        DestinationAddress: "وجهة الاختبار",
        DestinationLatitude: latitude + 0.02,
        DestinationLongitude: longitude + 0.02,
        CustomerPrice: 1_000m,
        IdempotencyKey: "realtime-event-ride"));
    AssertSuccess(rideResult, "ride request before rideAvailable event");
    var rideId = Convert.ToInt32(rideResult.Data!.GetType().GetProperty("Id")!.GetValue(rideResult.Data));
    Assert(publisher.AvailableRecipients.Count == 1, "rideAvailable targets the matched driver only");
    var available = publisher.AvailableRecipients.Single();
    AssertEqual(candidateDriver.Id, available.UserId, "rideAvailable recipient is the eligible driver");
    AssertEqual(rideId, Convert.ToInt32(available.Ride["id"]), "rideAvailable contains the saved ride id");
    Assert(Equals(false, available.Ride["destinationAvailable"]), "rideAvailable hides the exact destination from an unassigned driver");
    Assert(available.Ride["destinationLatitude"] is null, "rideAvailable omits destination coordinates");

    var offerService = new RideOfferService(
        db, configuration, new TestCurrentUser(candidateDriver.Id), publisher);
    var offerResult = await ExecuteAsync(offerService, "add", new RideOfferModel(
        RideId: rideId, Amount: 950m, ValidForSeconds: 90));
    AssertSuccess(offerResult, "driver offer before offerReceived event");
    Assert(publisher.OfferReceivedCustomerId == fixture.CustomerId, "offerReceived targets the ride customer");
    AssertEqual(rideId, publisher.ReceivedOffer!.RideId, "offerReceived contains the saved offer's ride id");
    AssertEqual(OfferStatus.Pending, publisher.ReceivedOffer.Status, "offerReceived carries the persisted pending status");

    var offerAction = new RideOfferAction(
        db, configuration, new TestCurrentUser(fixture.CustomerId), publisher);
    AssertSuccess(await ExecuteAsync(offerAction, "accept", new RideOfferActionModel(
        RideId: rideId, OfferId: publisher.ReceivedOffer.Id)), "customer accepts offer before offerUpdated event");
    var acceptedUpdate = publisher.UpdatedOffers.Single(item =>
        item.Status == OfferStatus.Accepted && item.AssignedDriverId == candidateDriver.Id);
    var assignedRideSnapshot = acceptedUpdate.RideSnapshot
        ?? throw new InvalidOperationException("The winning driver must receive the assigned ride snapshot.");
    Assert(Equals(true, assignedRideSnapshot["destinationAvailable"]),
        "the assigned driver receives destination details after acceptance");

    var secondRideResult = await ExecuteAsync(rideService, "add", new RideModel(
        ServiceKindId: fixture.ServiceKindId,
        ServiceCatalogItemId: fixture.ServiceCatalogItemId,
        PickupLabel: "نقطة ثانية",
        PickupAddress: "نقطة ثانية",
        PickupLatitude: latitude,
        PickupLongitude: longitude,
        DestinationLabel: "وجهة ثانية",
        DestinationAddress: "وجهة ثانية",
        DestinationLatitude: latitude + 0.02,
        DestinationLongitude: longitude + 0.02,
        CustomerPrice: 1_000m,
        IdempotencyKey: "realtime-event-cancel"));
    AssertSuccess(secondRideResult, "second ride request before rideUnavailable event");
    var secondRideId = Convert.ToInt32(
        secondRideResult.Data!.GetType().GetProperty("Id")!.GetValue(secondRideResult.Data));
    var searchAction = new RideSearchAction(
        db, configuration, new TestCurrentUser(fixture.CustomerId), publisher);
    AssertSuccess(await ExecuteAsync(searchAction, "cancel",
        new RideSearchActionModel(RideId: secondRideId)),
        "customer closes search before rideUnavailable event");
    Assert(publisher.RideUnavailableRideId == secondRideId &&
           publisher.RideUnavailableCustomerId == fixture.CustomerId,
        "rideUnavailable targets the customer session after the search is closed");
}

static async Task TrackingHubRequiresAuthenticatedRideMembershipAsync(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    Fixture fixture,
    string databaseName)
{
    var ride = await AddRideAsync(
        db, fixture, RideStatus.DriverAssigned, 900m, 100m, 0m);
    const string shareToken = "integration-test-live-share-token";
    var sharedRide = new YemenDrive.Database.Entities.RideSafetyShare
    {
        RideId = ride.Id,
        OwnerUserId = fixture.CustomerId,
        TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shareToken))),
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
    };
    db.RideSafetyShares.Add(sharedRide);
    await db.SaveChangesAsync();
    var unrelatedUser = new YemenDrive.Database.Entities.User
    {
        PhoneNumber = "+967700000097",
        DisplayName = "Hub outsider",
        PasswordHash = "integration-test",
        Role = UserRole.Customer
    };
    db.Users.Add(unrelatedUser);
    await db.SaveChangesAsync();
    db.Wallets.Add(new Wallet { UserId = unrelatedUser.Id, Balance = 0m, Currency = "YER" });
    await db.SaveChangesAsync();

    var sessions = new AuthSessionService();
    var customerSession = await sessions.CreateAsync(
        db, await db.Users.SingleAsync(item => item.Id == fixture.CustomerId), "hub-customer", CancellationToken.None);
    var driverSession = await sessions.CreateAsync(
        db, await db.Users.SingleAsync(item => item.Id == fixture.DriverId), "hub-driver", CancellationToken.None);
    var adminSession = await sessions.CreateAsync(
        db, await db.Users.SingleAsync(item => item.Id == fixture.AdminId), "hub-admin", CancellationToken.None);
    var outsiderSession = await sessions.CreateAsync(
        db, unrelatedUser, "hub-outsider", CancellationToken.None);

    var connectionString = $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ContentRootPath = Path.GetTempPath(),
        ApplicationName = typeof(TrackingHub).Assembly.GetName().Name
    });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(configuration);
    builder.Services.AddDbContext<YemenDriveDbContext>(options => options.UseSqlServer(connectionString));
    builder.Services.AddScoped<AuthSessionService>();
    builder.Services.AddScoped<TrackingHubAccessPolicy>();
    builder.Services.AddSingleton<NearbyDriverSubscriptionStore>();
    builder.Services.AddScoped<IRideRealtimePublisher, SignalRRideRealtimePublisher>();
    builder.Services.AddSignalR();

    await using var app = builder.Build();
    app.UseMiddleware<AuthSessionAuthenticationMiddleware>();
    app.MapHub<TrackingHub>("/hubs/tracking", options =>
        options.CloseOnAuthenticationExpiration = true);
    app.MapHub<SharedRideHub>("/hubs/shared-rides");
    await app.StartAsync();
    try
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var baseUri = new Uri(address);
        using var http = new HttpClient();
        var anonymousNegotiation = await http.PostAsync(
            new Uri(baseUri, "/hubs/tracking/negotiate?negotiateVersion=1"),
            content: null);
        Assert(anonymousNegotiation.StatusCode == HttpStatusCode.Unauthorized,
            "an unauthenticated SignalR negotiation is rejected");

        await using var customer = await ConnectTrackingClientAsync(baseUri, customerSession.AccessToken);
        await using var driver = await ConnectTrackingClientAsync(baseUri, driverSession.AccessToken);
        await using var admin = await ConnectTrackingClientAsync(baseUri, adminSession.AccessToken);
        await using var outsider = await ConnectTrackingClientAsync(baseUri, outsiderSession.AccessToken);
        await using var shareViewer = await ConnectSharedRideClientAsync(baseUri, shareToken);
        Assert(await customer.InvokeErrorAsync("JoinRide", ride.Id) is null,
            "an authenticated customer can connect and join their ride");
        Assert(await driver.InvokeErrorAsync("JoinRide", ride.Id) is null,
            "an authenticated assigned driver can connect and join their ride");
        Assert(await outsider.InvokeErrorAsync("JoinRide", ride.Id) is not null,
            "an authenticated unrelated user is rejected from the ride group");
        Assert(await admin.InvokeErrorAsync("JoinOperations") is null,
            "an active administrator can join the operations group");
        Assert(await customer.InvokeErrorAsync("JoinNearbyDrivers", new
        {
            serviceKindId = fixture.ServiceKindId,
            serviceCatalogItemId = fixture.ServiceCatalogItemId,
            pickupLatitude = 15.3694,
            pickupLongitude = 44.1910,
            radiusMeters = 3000,
            initialDriverIds = new[] { fixture.DriverId }
        }) is null,
            "an active customer can subscribe to a service and radius limited nearby-driver map");
        var sampleTime = DateTime.UtcNow;
        Assert(await driver.InvokeErrorAsync("PublishDriverLocation", new
        {
            rideId = ride.Id,
            latitude = 15.3695,
            longitude = 44.1911,
            bearing = 90,
            speed = 4.5,
            accuracy = 100,
            observedAtUtc = sampleTime
        }) is not null,
            "the Hub rejects GPS samples whose accuracy exceeds the accepted threshold");
        Assert(await driver.InvokeErrorAsync("PublishDriverLocation", new
        {
            rideId = int.MaxValue,
            latitude = 15.3695,
            longitude = 44.1911,
            bearing = 90,
            speed = 4.5,
            accuracy = 8,
            observedAtUtc = sampleTime
        }) is not null,
            "the Hub rejects GPS samples for a ride not assigned to the driver");
        var locationPublishError = await driver.InvokeErrorAsync("PublishDriverLocation", new
        {
            rideId = ride.Id,
            latitude = 15.3695,
            longitude = 44.1911,
            bearing = 90,
            speed = 4.5,
            accuracy = 8,
            observedAtUtc = sampleTime
        });
        Assert(locationPublishError is null,
            $"the assigned driver can publish a recent accurate GPS sample ({locationPublishError})");
        Assert(await customer.ReceiveEventAsync("driverLocationUpdated"),
            "the ride customer receives the live driver-location event");
        Assert(await admin.ReceiveEventAsync("driverLocationUpdated"),
            "the operations group receives active-trip driver-location events");
        Assert(await shareViewer.ReceiveEventAsync("sharedRideLocationUpdated"),
            "an authorized share-link viewer receives live driver locations");
        Assert(await customer.ReceiveEventAsync("nearbyDriverAvailabilityChanged"),
            "the nearby map removes an assigned driver from its available markers");
        var savedLocation = await db.DriverLiveLocations.AsNoTracking()
            .SingleAsync(item => item.DriverId == fixture.DriverId);
        Assert(savedLocation.Latitude == 15.3695 && savedLocation.Longitude == 44.1911,
            "the SignalR GPS sample updates the driver's current-location snapshot");

        db.LocationUpdates.Add(new LocationUpdate
        {
            RideId = ride.Id,
            ActorId = fixture.DriverId,
            Latitude = 15.3795,
            Longitude = 44.2011,
            ObservedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await using var realtimeScope = app.Services.CreateAsyncScope();
        var realtimePublisher = realtimeScope.ServiceProvider.GetRequiredService<IRideRealtimePublisher>();

        var notificationCapture = new NotificationSaveCaptureInterceptor();
        var notificationOptions = new DbContextOptionsBuilder<YemenDriveDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(notificationCapture)
            .Options;
        await using var notificationDb = new YemenDriveDbContext(notificationOptions);
        notificationDb.Notifications.Add(new Notification
        {
            UserId = fixture.CustomerId,
            Type = NotificationType.RideStatus,
            Title = "Realtime notification test",
            Body = "A saved notification should invalidate the open list."
        });
        await notificationDb.SaveChangesAsync();
        var capturedNotifications = await notificationCapture.TakeCommittedAsync(
            notificationDb, CancellationToken.None);
        Assert(capturedNotifications.Count == 1 &&
               capturedNotifications[0].UserId == fixture.CustomerId,
            "the save interceptor captures persisted notifications for their recipient");
        await realtimePublisher.PublishNotificationsCreatedAsync(
            capturedNotifications, CancellationToken.None);
        Assert(await customer.ReceiveEventAsync("notificationCreated"),
            "the notification recipient receives notificationCreated over SignalR");

        var storedNotification = await notificationDb.Notifications.SingleAsync();
        storedNotification.IsRead = true;
        await notificationDb.SaveChangesAsync();
        Assert((await notificationCapture.TakeCommittedAsync(
                   notificationDb, CancellationToken.None)).Count == 0,
            "updating an existing notification does not produce notificationCreated");

        await using (var transaction = await notificationDb.Database.BeginTransactionAsync())
        {
            notificationDb.Notifications.Add(new Notification
            {
                UserId = fixture.CustomerId,
                Type = NotificationType.System,
                Title = "Rolled back notification",
                Body = "This notification must not be broadcast."
            });
            await notificationDb.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        Assert((await notificationCapture.TakeCommittedAsync(
                   notificationDb, CancellationToken.None)).Count == 0,
            "a notification from a rolled-back transaction is not published");

        var customerChat = new YemenDrive.Application.Communication.RideMessage(
            db, configuration, new TestCurrentUser(fixture.CustomerId), realtimePublisher);
        var firstMessageResult = await ExecuteAsync(
            customerChat,
            "add",
            new YemenDrive.Application.Communication.RideMessageModel(
                RideId: ride.Id, Content: "First realtime message"));
        AssertSuccess(firstMessageResult, "customer message before rideMessageCreated event");
        var firstMessageId = Convert.ToInt32(
            firstMessageResult.Data!.GetType().GetProperty("Id")!.GetValue(firstMessageResult.Data));
        Assert(await customer.ReceiveEventAsync("rideMessageCreated"),
            "the sender receives the persisted rideMessageCreated event");
        Assert(await driver.ReceiveEventAsync("rideMessageCreated"),
            "the other ride participant receives the persisted rideMessageCreated event");
        Assert(await db.CommunicationMessages.AsNoTracking().AnyAsync(item =>
                item.Id == firstMessageId && item.Content == "First realtime message"),
            "the message exists in the database before its realtime event is observed");

        var driverChat = new YemenDrive.Application.Communication.RideMessage(
            db, configuration, new TestCurrentUser(fixture.DriverId), realtimePublisher);
        var secondMessageResult = await ExecuteAsync(
            driverChat,
            "add",
            new YemenDrive.Application.Communication.RideMessageModel(
                RideId: ride.Id, Content: "Second realtime message"));
        AssertSuccess(secondMessageResult, "driver message before rideMessageCreated event");
        Assert(await customer.ReceiveEventAsync("rideMessageCreated"),
            "the customer receives a message sent by the driver");
        Assert(await driver.ReceiveEventAsync("rideMessageCreated"),
            "the driver receives their persisted message");

        var latestMessage = await db.CommunicationMessages.AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id)
            .FirstAsync(item => item.RideId == ride.Id);
        var messageReport = new YemenDrive.Application.Communication.RideMessageReport(
            db, configuration, new TestCurrentUser(fixture.CustomerId));
        var firstPageResult = await ExecuteAsync(
            messageReport,
            "list",
            new YemenDrive.Application.Communication.RideMessageModel(
                RideId: ride.Id, PageSize: 1));
        AssertSuccess(firstPageResult, "latest chat page");
        var firstPage = ((System.Collections.IEnumerable)firstPageResult.Data!).Cast<object>().ToArray();
        Assert(firstPage.Length == 1 &&
               Equals("Second realtime message", firstPage[0].GetType()
                   .GetProperty("Content")!.GetValue(firstPage[0])),
            "the first chat page contains the newest message instead of the oldest 500");
        var secondPageResult = await ExecuteAsync(
            messageReport,
            "list",
            new YemenDrive.Application.Communication.RideMessageModel(
                RideId: ride.Id,
                PageSize: 1,
                BeforeId: latestMessage.Id,
                BeforeCreatedAtUtc: latestMessage.CreatedAtUtc));
        AssertSuccess(secondPageResult, "older chat page");
        var secondPage = ((System.Collections.IEnumerable)secondPageResult.Data!).Cast<object>().ToArray();
        Assert(secondPage.Length == 1 &&
               Equals("First realtime message", secondPage[0].GetType()
                   .GetProperty("Content")!.GetValue(secondPage[0])),
            "the chat cursor returns the previous page in chronological order");

        var safetyIncident = new YemenDrive.Application.Safety.SafetyIncident(
            db, configuration, new TestCurrentUser(fixture.CustomerId), realtimePublisher);
        var safetyResult = await ExecuteAsync(
            safetyIncident,
            "add",
            new SafetyIncidentModel(RideId: ride.Id, Message: "Integration test safety alert"));
        AssertSuccess(safetyResult, "safety incident before realtime event");
        var safetyIncidentId = Convert.ToInt32(
            safetyResult.Data!.GetType().GetProperty("Id")!.GetValue(safetyResult.Data));
        var storedIncident = await db.SafetyIncidents.AsNoTracking()
            .SingleAsync(item => item.Id == safetyIncidentId);
        Assert(storedIncident.Latitude == ride.PickupLatitude &&
               storedIncident.LocationObservedAtUtc is null,
            "a safety alert does not use the other party's location as the reporter's GPS point");
        Assert(await admin.ReceiveEventAsync("safetyIncidentCreated"),
            "operations receives a safetyIncidentCreated event after the alert is saved");
        var safetyAdminAction = new YemenDrive.Application.Safety.SafetyIncident(
            db, configuration, new TestCurrentUser(fixture.AdminId), realtimePublisher);
        AssertSuccess(await ExecuteAsync(
            safetyAdminAction,
            "accept",
            new SafetyIncidentModel(Id: safetyIncidentId)),
            "safety incident acknowledgement before realtime event");
        Assert(await admin.ReceiveEventAsync("safetyIncidentUpdated"),
            "operations receives a safetyIncidentUpdated event after the acknowledgement is saved");
        var revokeShare = new YemenDrive.Application.Safety.RideSafetyShare(
            db, configuration, new TestCurrentUser(fixture.CustomerId), realtimePublisher);
        AssertSuccess(await ExecuteAsync(
            revokeShare,
            "cancel",
            new RideSafetyShareModel(Id: sharedRide.Id)),
            "shared ride revocation before realtime event");
        Assert(await shareViewer.ReceiveEventAsync("sharedRideRevoked"),
            "revoking a share link stops its active viewer immediately");

        using var scope = app.Services.CreateScope();
        var sessionService = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        await sessionService.RevokeAllForUserAsync(db, unrelatedUser.Id, CancellationToken.None);
        Assert(await outsider.ReceiveEventAsync("sessionRevoked"),
            "revoking a user session notifies its active Hub connection");
    }
    finally
    {
        await app.StopAsync();
    }
}

static async Task<SignalRTestConnection> ConnectTrackingClientAsync(Uri baseUri, string accessToken)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var negotiateResponse = await http.PostAsync(
        new Uri(baseUri, "/hubs/tracking/negotiate?negotiateVersion=1"),
        content: null);
    Assert(negotiateResponse.IsSuccessStatusCode,
        $"authenticated SignalR negotiation succeeds ({(int)negotiateResponse.StatusCode})");
    using var negotiateJson = JsonDocument.Parse(await negotiateResponse.Content.ReadAsStringAsync());
    var connectionToken = negotiateJson.RootElement.GetProperty("connectionToken").GetString()
        ?? throw new InvalidOperationException("SignalR negotiation did not return a connection token.");

    var uri = new UriBuilder(baseUri)
    {
        Scheme = "ws",
        Path = "/hubs/tracking",
        Query = $"id={Uri.EscapeDataString(connectionToken)}&transport=WebSockets&access_token={Uri.EscapeDataString(accessToken)}"
    }.Uri;
    var socket = new ClientWebSocket();
    await socket.ConnectAsync(uri, CancellationToken.None);
    var connection = new SignalRTestConnection(socket);
    await connection.HandshakeAsync();
    return connection;
}

static async Task<SignalRTestConnection> ConnectSharedRideClientAsync(Uri baseUri, string shareToken)
{
    using var http = new HttpClient();
    var encodedToken = Uri.EscapeDataString(shareToken);
    var negotiateResponse = await http.PostAsync(
        new Uri(baseUri, $"/hubs/shared-rides/negotiate?negotiateVersion=1&token={encodedToken}"),
        content: null);
    Assert(negotiateResponse.IsSuccessStatusCode,
        $"shared-trip SignalR negotiation succeeds ({(int)negotiateResponse.StatusCode})");
    using var negotiateJson = JsonDocument.Parse(await negotiateResponse.Content.ReadAsStringAsync());
    var connectionToken = negotiateJson.RootElement.GetProperty("connectionToken").GetString()
        ?? throw new InvalidOperationException("Shared-trip SignalR negotiation did not return a connection token.");

    var uri = new UriBuilder(baseUri)
    {
        Scheme = "ws",
        Path = "/hubs/shared-rides",
        Query = $"id={Uri.EscapeDataString(connectionToken)}&transport=WebSockets&token={encodedToken}"
    }.Uri;
    var socket = new ClientWebSocket();
    await socket.ConnectAsync(uri, CancellationToken.None);
    var connection = new SignalRTestConnection(socket);
    await connection.HandshakeAsync();
    return connection;
}

static async Task ServerSessionsRotateAndRevokeCredentialsAsync(YemenDriveDbContext db, Fixture fixture)
{
    var service = new AuthSessionService();
    var customer = await db.Users.SingleAsync(item => item.Id == fixture.CustomerId);
    var issued = await service.CreateAsync(db, customer, "customer-device-123", CancellationToken.None);
    Assert(issued.AccessToken.Length >= 40 && issued.RefreshToken.Length >= 40, "server sessions issue high-entropy opaque tokens");

    var stored = await db.AuthSessions.SingleAsync(item => item.AccessTokenHash == Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(issued.AccessToken))));
    Assert(stored.AccessTokenHash != issued.AccessToken && stored.RefreshTokenHash != issued.RefreshToken, "only token hashes are persisted");
    Assert(stored.AccessTokenHash.Length == 64 && stored.RefreshTokenHash.Length == 64, "token hashes are fixed-size SHA-256 values");

    var initialSession = await service.AuthenticateAsync(db, issued.AccessToken, CancellationToken.None);
    Assert(initialSession?.UserId == customer.Id, "an active access token resolves to its account");
    var rotated = await service.RefreshAsync(db, issued.RefreshToken, CancellationToken.None);
    Assert(rotated is not null, "an unused refresh token rotates successfully");
    Assert(rotated!.Tokens.AccessToken != issued.AccessToken && rotated.Tokens.RefreshToken != issued.RefreshToken, "refresh rotates both credentials");
    AssertEqual(issued.SessionExpiresAtUtc, rotated.Tokens.SessionExpiresAtUtc, "rotation does not extend the original absolute session expiry");
    Assert(await service.AuthenticateAsync(db, issued.AccessToken, CancellationToken.None) is null, "the prior access token is invalid after rotation");
    Assert((await service.AuthenticateAsync(db, rotated.Tokens.AccessToken, CancellationToken.None))?.UserId == customer.Id, "the replacement access token is active");

    Assert(await service.RefreshAsync(db, issued.RefreshToken, CancellationToken.None) is null, "a used refresh token cannot be replayed");
    Assert(await service.AuthenticateAsync(db, rotated.Tokens.AccessToken, CancellationToken.None) is null, "refresh-token replay revokes the entire session family");

    var logoutSession = await service.CreateAsync(db, customer, "customer-device-456", CancellationToken.None);
    await service.RevokeCurrentAsync(db, logoutSession.AccessToken, CancellationToken.None);
    Assert(await service.AuthenticateAsync(db, logoutSession.AccessToken, CancellationToken.None) is null, "server logout revokes the active session");

    var passwordResetSession = await service.CreateAsync(db, customer, "customer-device-789", CancellationToken.None);
    await service.RevokeAllForUserAsync(db, customer.Id, CancellationToken.None);
    Assert(await service.AuthenticateAsync(db, passwordResetSession.AccessToken, CancellationToken.None) is null, "credential changes revoke every account session");

    var expiringSession = await service.CreateAsync(db, customer, "customer-device-exp", CancellationToken.None);
    var expiringRow = await db.AuthSessions.SingleAsync(item => item.RefreshTokenHash == Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expiringSession.RefreshToken))));
    expiringRow.AccessExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
    await db.SaveChangesAsync();
    Assert(await service.AuthenticateAsync(db, expiringSession.AccessToken, CancellationToken.None) is null, "expired access tokens are rejected");

    var admin = await db.Users.SingleAsync(item => item.Id == fixture.AdminId);
    var adminSession = await service.CreateAsync(db, admin, deviceId: null, CancellationToken.None);
    var adminLifetime = adminSession.SessionExpiresAtUtc - DateTime.UtcNow;
    Assert(adminLifetime > TimeSpan.FromHours(7) && adminLifetime <= TimeSpan.FromHours(8), "administrator sessions have an eight-hour absolute lifetime");
}

static void OtpChallengesAreConsoleOnlyInDevelopmentAndLimitedToFiveAttempts()
{
    var originalOutput = Console.Out;
    using var capturedOutput = new StringWriter();
    try
    {
        Console.SetOut(capturedOutput);
        var productionStore = new OtpChallengeStore(isDevelopment: false);
        Assert(productionStore.TryCreate("700000001", "signIn") is null, "OTP cannot be created outside Development while no SMS provider is configured");
        Assert(capturedOutput.ToString().Length == 0, "production OTP requests must not print to the console");

        var developmentStore = new OtpChallengeStore(isDevelopment: true);
        var firstChallenge = developmentStore.TryCreate("700000001", "signIn")!.Value;
        var firstCode = ExtractLastOtp(capturedOutput.ToString());
        Assert(firstCode.Length == 6, "development OTP must be printed for local testing");
        for (var attempt = 0; attempt < 5; attempt++)
            Assert(developmentStore.Verify(firstChallenge.ChallengeId, "700000001", "000000", "signIn") is null, "incorrect OTP must be rejected");
        Assert(developmentStore.Verify(firstChallenge.ChallengeId, "700000001", firstCode, "signIn") is null, "a challenge is locked after five failed attempts");

        var secondChallenge = developmentStore.TryCreate("700000002", "signIn")!.Value;
        var secondCode = ExtractLastOtp(capturedOutput.ToString());
        var verificationToken = developmentStore.Verify(secondChallenge.ChallengeId, "700000002", secondCode, "signIn");
        Assert(verificationToken is not null, "a valid development OTP can be verified");
        Assert(developmentStore.ConsumeVerificationToken(verificationToken!, "700000002", "signIn"), "OTP verification grant can be consumed once");
        Assert(!developmentStore.ConsumeVerificationToken(verificationToken!, "700000002", "signIn"), "OTP verification grant cannot be replayed");
    }
    finally
    {
        Console.SetOut(originalOutput);
    }
}

static string ExtractLastOtp(string output)
{
    var matches = System.Text.RegularExpressions.Regex.Matches(output, @": (\d{6})");
    return matches.Count == 0 ? string.Empty : matches[matches.Count - 1].Groups[1].Value;
}

static async Task TrustedDeviceCredentialsRequireAValidUnexpiredServerTokenAsync(
    YemenDriveDbContext db,
    Fixture fixture)
{
    var credentials = new TrustedDeviceCredentialService();
    const string deviceId = "auth-test-device-01";
    var rawToken = await credentials.IssueAsync(db, fixture.CustomerId, deviceId, CancellationToken.None);

    Assert(rawToken.Length == 64, "trusted-device token must be a random 256-bit value");
    Assert(await credentials.IsTrustedAsync(db, fixture.CustomerId, deviceId, rawToken, CancellationToken.None), "the issued token trusts only its bound device");
    Assert(!await credentials.IsTrustedAsync(db, fixture.CustomerId, "auth-test-device-02", rawToken, CancellationToken.None), "trusted token cannot be replayed for another device");
    Assert(!await credentials.IsTrustedAsync(db, fixture.CustomerId, deviceId, rawToken + "x", CancellationToken.None), "a modified trusted token is rejected");

    var stored = await db.TrustedDevices.SingleAsync(item => item.UserId == fixture.CustomerId && item.DeviceId == deviceId);
    Assert(stored.TokenHash != rawToken, "only a token hash is stored in the database");
    stored.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
    await db.SaveChangesAsync();
    Assert(!await credentials.IsTrustedAsync(db, fixture.CustomerId, deviceId, rawToken, CancellationToken.None), "expired trust is rejected");

    rawToken = await credentials.IssueAsync(db, fixture.CustomerId, deviceId, CancellationToken.None);
    await TrustedDeviceCredentialService.RevokeAllAsync(db, fixture.CustomerId, CancellationToken.None);
    await db.SaveChangesAsync();
    Assert(!await credentials.IsTrustedAsync(db, fixture.CustomerId, deviceId, rawToken, CancellationToken.None), "revoked trust is rejected");
}

static async Task GeneralLedgerFoundationIsBalancedAndImmutableAsync(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    Fixture fixture)
{
    var provisioning = new FinancialAccountProvisioningService(db);
    var setup = new AccountingSetup(db, configuration, new TestCurrentUser(fixture.AdminId), provisioning);
    AssertSuccess(await ExecuteAsync(setup, "add", new AccountingSetupModel()), "accounting chart setup");
    Assert(await db.LedgerAccounts.CountAsync() >= 6, "standard accounting accounts must exist");

    var driverParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.User && x.EntityId == fixture.DriverId);
    var customerParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.Wallet && x.EntityId == fixture.CustomerWalletId);
    var driverAccount = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == driverParty.Id && x.Purpose == LedgerAccountPurpose.DriverCurrentAccount);
    var customerWalletAccount = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == customerParty.Id && x.Purpose == LedgerAccountPurpose.CustomerWallet);
    var accounts = await db.LedgerAccounts.ToDictionaryAsync(x => x.Code);

    var posting = new AccountingPostingService(db);
    var posted = await posting.PostAsync(new AccountingPostingRequest(
        JournalEntryType.RideCashCollection, "TEST-RIDE-CASH-1", "اختبار تحصيل نقدي متوازن", "YER",
        [
            new AccountingPostingLine(driverAccount.Id, Debit: 1800m, UserId: fixture.DriverId),
            new AccountingPostingLine(accounts["4100"].Id, Credit: 300m),
            new AccountingPostingLine(accounts["4200"].Id, Credit: 1500m),
            new AccountingPostingLine(accounts["5100"].Id, Debit: 150m),
            new AccountingPostingLine(driverAccount.Id, Credit: 150m, UserId: fixture.DriverId)
        ],
        SourceType: "FinancialIntegrationTest", SourceId: "cash-1", CreatedByUserId: fixture.AdminId), CancellationToken.None);
    Assert(!posted.AlreadyPosted, "first ledger posting must be new");
    Assert((await posting.PostAsync(new AccountingPostingRequest(
        JournalEntryType.RideCashCollection, "TEST-RIDE-CASH-1", "اختبار تحصيل نقدي متوازن", "YER",
        [new AccountingPostingLine(driverAccount.Id, Debit: 1800m), new AccountingPostingLine(accounts["4200"].Id, Credit: 1800m)],
        SourceType: "FinancialIntegrationTest", SourceId: "cash-1"), CancellationToken.None)).AlreadyPosted,
        "same source must be idempotent");
    Assert(await db.JournalLines.CountAsync(x => x.JournalEntryId == posted.JournalEntryId) == 5, "balanced cash entry has five lines");

    var unbalancedRejected = false;
    try { await posting.PostAsync(new AccountingPostingRequest(JournalEntryType.ManualAdjustment, "TEST-BAD", "قيد غير متوازن", "YER", [new AccountingPostingLine(driverAccount.Id, Debit: 100m), new AccountingPostingLine(accounts["4200"].Id, Credit: 90m)]), CancellationToken.None); }
    catch (ServiceException exception) when (exception.Code == "journal_entry_unbalanced") { unbalancedRejected = true; }
    Assert(unbalancedRejected, "unbalanced ledger entry must be rejected");

    db.ChangeTracker.Clear();
    var storedEntry = await db.JournalEntries.SingleAsync(x => x.Id == posted.JournalEntryId);
    storedEntry.Description = "محاولة تعديل";
    var immutable = false;
    try { await db.SaveChangesAsync(); }
    catch (InvalidOperationException) { immutable = true; db.ChangeTracker.Clear(); }
    Assert(immutable, "posted journal entry must be immutable");

    var statement = new AccountStatementReport(db, configuration, new TestCurrentUser(fixture.AdminId));
    AssertSuccess(await ExecuteAsync(statement, "report", new AccountStatementModel()), "account statement report");
}

static async Task FinancialAccountsAreProvisionedForUsersAndServiceKindsAsync(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    Fixture fixture)
{
    var provisioning = new FinancialAccountProvisioningService(db);
    var setup = new AccountingSetup(db, configuration, new TestCurrentUser(fixture.AdminId), provisioning);
    AssertSuccess(await ExecuteAsync(setup, "add", new AccountingSetupModel()), "legacy financial account provisioning");

    var existingCustomerWallet = await db.Wallets.SingleAsync(x => x.UserId == fixture.CustomerId);
    var existingCustomerWalletParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.Wallet && x.EntityId == existingCustomerWallet.Id);
    Assert(await db.LedgerAccounts.AnyAsync(x => x.FinancialPartyId == existingCustomerWalletParty.Id && x.Purpose == LedgerAccountPurpose.CustomerWallet), "legacy customer wallet has a ledger account");
    var existingDriverParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.User && x.EntityId == fixture.DriverId);
    Assert(await db.LedgerAccounts.AnyAsync(x => x.FinancialPartyId == existingDriverParty.Id && x.Purpose == LedgerAccountPurpose.DriverCurrentAccount), "legacy driver has a current account");

    var users = new UserService(db, configuration, new TestCurrentUser(fixture.AdminId), provisioning);
    AssertSuccess(await ExecuteAsync(users, "add", new YemenDrive.Application.Users.UserModel(DisplayName: "عميل حسابي جديد", PhoneNumber: "+967700000099", Password: "integration-test", Role: UserRole.Customer)), "new customer financial account provisioning");
    AssertSuccess(await ExecuteAsync(users, "add", new YemenDrive.Application.Users.UserModel(DisplayName: "سائق حسابي جديد", PhoneNumber: "+967700000098", Password: "integration-test", Role: UserRole.Driver)), "new driver financial account provisioning");
    var newCustomer = await db.Users.Include(x => x.Wallet).SingleAsync(x => x.PhoneNumber == "+967700000099");
    var newDriver = await db.Users.Include(x => x.Wallet).SingleAsync(x => x.PhoneNumber == "+967700000098");
    var newCustomerWalletParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.Wallet && x.EntityId == newCustomer.Wallet!.Id);
    var newDriverParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.User && x.EntityId == newDriver.Id);
    Assert(await db.LedgerAccounts.AnyAsync(x => x.FinancialPartyId == newCustomerWalletParty.Id && x.Purpose == LedgerAccountPurpose.CustomerWallet), "new customer wallet ledger account");
    Assert(await db.LedgerAccounts.AnyAsync(x => x.FinancialPartyId == newDriverParty.Id && x.Purpose == LedgerAccountPurpose.DriverCurrentAccount), "new driver current ledger account");

    var kinds = new ServiceKindService(db, configuration, provisioning);
    AssertSuccess(await ExecuteAsync(kinds, "add", new YemenDrive.Application.Catalog.ServiceKindModel(Code: "financial-auto-kind", NameAr: "نوع اختبار الحسابات")), "service-kind financial accounts");
    var kind = await db.ServiceKinds.SingleAsync(x => x.Code == "financial-auto-kind");
    var kindParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.ServiceKind && x.EntityId == kind.Id);
    AssertEqual(2, await db.LedgerAccounts.CountAsync(x => x.FinancialPartyId == kindParty.Id && (x.Purpose == LedgerAccountPurpose.ServiceFee || x.Purpose == LedgerAccountPurpose.ServiceCollection)), "service kind has fee and collection accounts");
}

static async Task<Fixture> SeedAsync(YemenDriveDbContext db)
{
    var customer = new User { PhoneNumber = "+967700000001", DisplayName = "Financial Test Customer", PasswordHash = "test", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "+967700000002", DisplayName = "Financial Test Driver", PasswordHash = "test", Role = UserRole.Driver };
    var admin = new User { PhoneNumber = "+967700000003", DisplayName = "Financial Test Admin", PasswordHash = "test", Role = UserRole.Admin };
    var kind = new ServiceKind { Code = "financial-test-kind", NameAr = "اختبار مالي", IsActive = true, IsDefault = true };
    db.AddRange(customer, driver, admin, kind);
    await db.SaveChangesAsync();

    var service = new RideServiceCatalogItem
    {
        Code = "financial-test-service", NameAr = "خدمة اختبار", ServiceKindId = kind.Id,
        ArrivalMinutes = 1, BasePrice = 0, Rating = 0, Seats = 4, DescriptionAr = "خدمة اختبار", IsActive = true
    };
    db.ServiceCatalogItems.Add(service);
    await db.SaveChangesAsync();

    db.DriverProfiles.Add(new DriverProfile
    {
        UserId = driver.Id,
        ServiceKindId = kind.Id,
        ServiceCatalogItemId = service.Id,
        VehicleModel = "Test vehicle",
        PlateNumber = "TEST-001",
        CommissionRate = 0.05m,
        IsAvailable = true,
        Rating = 5m
    });
    await db.SaveChangesAsync();

    var customerWallet = new Wallet { UserId = customer.Id, Balance = 3_000m, Currency = "YER" };
    var driverWallet = new Wallet { UserId = driver.Id, Balance = 100m, Currency = "YER" };
    db.Wallets.AddRange(customerWallet, driverWallet);
    await db.SaveChangesAsync();
    return new Fixture(customer.Id, driver.Id, admin.Id, kind.Id, service.Id, customerWallet.Id, driverWallet.Id);
}

static async Task WalletPaymentIsAtomicAndIdempotentAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var ride = await AddRideAsync(db, fixture, RideStatus.Completed, 900m, 100m, 50m);
    var realtime = new RecordingRideRealtimePublisher();
    var payment = new Payment(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db), realtime);
    var request = new PaymentModel(ride.Id, ride.Id, null, 1_000m, "YER", "YemenDriveWallet", PaymentStatus.Paid, null, "wallet-payment-idempotency");
    AssertSuccess(await ExecuteAsync(payment, "add", request), "wallet payment");
    AssertSuccess(await ExecuteAsync(payment, "add", request), "wallet payment retry");
    db.ChangeTracker.Clear();
    AssertEqual(2_000m, await WalletBalanceAsync(db, fixture.CustomerId), "customer wallet after payment");
    AssertEqual(950m, await WalletBalanceAsync(db, fixture.DriverId), "driver wallet after payment");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid), "paid payment count");
    AssertEqual(2, await db.WalletTransactions.CountAsync(x => x.RideId == ride.Id), "wallet movements count");
    var walletJournal = await db.JournalEntries.Include(x => x.Lines).SingleAsync(x =>
        x.SourceType == "RideWalletPayment" && x.SourceId == $"ride:{ride.Id}");
    AssertEqual(JournalEntryType.RideWalletPayment, walletJournal.Type, "wallet payment journal type");
    AssertEqual(walletJournal.TotalDebit, walletJournal.TotalCredit, "wallet payment journal balanced");
    Assert(await db.JournalLines.AnyAsync(x => x.JournalEntryId == walletJournal.Id && x.RideId == ride.Id), "wallet journal lines linked to ride");
    AssertEqual(1, realtime.Payments.Count, "wallet payment publishes one realtime event including idempotent retries");
    AssertEqual("walletPaymentRecorded", realtime.Payments[0].Change, "wallet payment realtime change");
}

static async Task RideCancellationRequiresReasonAndFollowsReviewWorkflowAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var completedRide = await db.Rides.OrderBy(x => x.Id).FirstAsync(x => x.Status == RideStatus.Completed);
    var customerCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db));
    Assert(!(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: completedRide.Id, Reason: "لا أريد المتابعة"))).Success,
        "completed ride cannot be cancelled or refunded");

    // Before the driver starts heading to the customer, cancellation is
    // reviewed by the driver. DriverEnRoute is intentionally immediate in
    // the current product policy and is covered by the mobile flow.
    var ride = await AddRideAsync(db, fixture, RideStatus.DriverAssigned, 900m, 100m, 50m);
    Assert(!(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: " "))).Success,
        "cancellation reason is mandatory");
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: "تغيرت خططي اليوم")), "customer cancellation request");
    db.ChangeTracker.Clear();
    var first = await db.RideCancellationRequests.SingleAsync(x => x.RideId == ride.Id);
    AssertEqual(RideCancellationStatus.DriverReviewPending, first.Status, "pre-start cancellation is sent to driver");
    AssertEqual(RideStatus.CancellationPending, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "ride stops while cancellation is reviewed");
    var driverCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    AssertSuccess(await ExecuteAsync(driverCancellation, "reject", new RideCancellationRequestModel(Id: first.Id, Note: "لا أوافق")), "driver rejects cancellation");
    db.ChangeTracker.Clear();
    AssertEqual(RideCancellationStatus.DriverRejected, await db.RideCancellationRequests.Where(x => x.Id == first.Id).Select(x => x.Status).SingleAsync(), "driver rejection is recorded");
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: "ما زلت بحاجة للإلغاء")), "customer retries cancellation");
    var second = await db.RideCancellationRequests.OrderByDescending(x => x.Id).FirstAsync(x => x.RideId == ride.Id);
    AssertSuccess(await ExecuteAsync(driverCancellation, "refer", new RideCancellationRequestModel(Id: second.Id, Note: "مراجعة الإدارة مطلوبة")), "driver refers cancellation to administration");
    db.ChangeTracker.Clear();
    AssertEqual(RideCancellationStatus.AdminReviewPending, await db.RideCancellationRequests.Where(x => x.Id == second.Id).Select(x => x.Status).SingleAsync(), "referred cancellation awaits administration");
    var adminCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.AdminId), RideAccounting(db));
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: second.Id, Note: "تمت المراجعة")), "admin approves cancellation");
    AssertEqual(RideStatus.Cancelled, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "admin approval finalizes cancellation");
}

static async Task CashSettlementHandlesExcessAndShortageAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var excessRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    var realtime = new RecordingRideRealtimePublisher();
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db), realtime);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(excessRide.Id, 1_100m, "YER", null, "cash-excess")), "cash excess");
    db.ChangeTracker.Clear();
    AssertEqual(2_100m, await WalletBalanceAsync(db, fixture.CustomerId), "customer wallet after cash excess");
    AssertEqual(PaymentStatus.Pending, await db.DriverSettlements.Where(x => x.PaymentReference!.Contains($"ride:{excessRide.Id}:" )).Select(x => x.Status).SingleAsync(), "cash platform debt status");
    var cashJournal = await db.JournalEntries.Include(x => x.Lines).SingleAsync(x =>
        x.SourceType == "RideCashPayment" && x.SourceId == $"cash:ride:{excessRide.Id}:driver:{fixture.DriverId}");
    AssertEqual(JournalEntryType.RideCashCollection, cashJournal.Type, "cash payment journal type");
    AssertEqual(cashJournal.TotalDebit, cashJournal.TotalCredit, "cash payment journal balanced");
    var cashJournalAccountCodes = await db.JournalLines.Where(x => x.JournalEntryId == cashJournal.Id)
        .Join(db.LedgerAccounts, line => line.LedgerAccountId, account => account.Id, (_, account) => account.Code).ToListAsync();
    Assert(cashJournalAccountCodes.Any(code => code == $"4100-SK-{fixture.ServiceKindId}"), "cash journal uses service-kind fee account");
    Assert(cashJournalAccountCodes.Any(code => code == $"4200-SK-{fixture.ServiceKindId}"), "cash journal uses service-kind collection account");
    var debt = await db.DriverSettlements.SingleAsync(x => x.PaymentReference!.Contains($"ride:{excessRide.Id}:"));
    AssertEqual(150m, debt.PlatformCommission, "cash platform debt includes the configured driver commission but excludes customer excess");
    AssertEqual(100m, debt.Adjustments, "cash excess is recorded as a driver settlement adjustment");
    AssertEqual(-250m, debt.NetPayable, "cash excess increases the driver's debt to the customer and platform");
    var originalNetPayable = debt.NetPayable;
    var settlementPayment = new DriverSettlementPaymentService(db, configuration, new TestCurrentUser(fixture.AdminId), new AccountingPostingService(db), new FinancialAccountProvisioningService(db));
    var collection = new DriverSettlementPaymentModel(debt.Id, debt.Id, -debt.NetPayable, "YER", "CashToPlatform", "cash-debt-collection", "اختبار تحصيل");
    AssertSuccess(await ExecuteAsync(settlementPayment, "add", collection), "cash debt collection");
    Assert(! (await ExecuteAsync(settlementPayment, "add", collection)).Success, "duplicate collection reference must be rejected");
    db.ChangeTracker.Clear();
    AssertEqual(originalNetPayable, await db.DriverSettlements.Where(x => x.Id == debt.Id).Select(x => x.NetPayable).SingleAsync(), "source debt remains immutable");
    AssertEqual(-originalNetPayable, await db.DriverSettlementPayments.Where(x => x.DriverSettlementId == debt.Id).SumAsync(x => x.Amount), "cash debt collected amount");
    var settlementPaymentId = await db.DriverSettlementPayments.Where(x => x.DriverSettlementId == debt.Id)
        .Select(x => x.Id).SingleAsync();
    var settlementJournal = await db.JournalEntries.Include(x => x.Lines).SingleAsync(x =>
        x.SourceType == "DriverSettlementPayment" && x.SourceId == settlementPaymentId.ToString());
    AssertEqual(JournalEntryType.DriverSettlementCollection, settlementJournal.Type, "driver settlement journal type");
    AssertEqual(settlementJournal.TotalDebit, settlementJournal.TotalCredit, "driver settlement journal balanced");
    var settlementCodes = await db.JournalLines.Where(x => x.JournalEntryId == settlementJournal.Id)
        .Join(db.LedgerAccounts, line => line.LedgerAccountId, account => account.Id, (_, account) => account.Code).ToListAsync();
    Assert(settlementCodes.Contains("1000") && settlementCodes.Contains($"1100-DRV-{fixture.DriverId}"), "cash settlement uses platform cash and driver accounts");

    var shortageRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(shortageRide.Id, 900m, "YER", null, "cash-shortage")), "cash shortage approval request");
    db.ChangeTracker.Clear();
    var approval = await db.CashCollectionApprovals.SingleAsync(x => x.RideId == shortageRide.Id);
    AssertEqual(CashCollectionApprovalStatus.Pending, approval.Status, "cash shortage starts pending customer approval");
    AssertEqual(2_100m, await WalletBalanceAsync(db, fixture.CustomerId), "wallet stays unchanged before customer approval");
    AssertEqual(0, await db.PaymentTransactions.CountAsync(x => x.RideId == shortageRide.Id), "no payment before customer approval");

    var customerApproval = new CashCollectionApprovalService(db, configuration, new TestCurrentUser(fixture.CustomerId), realtime);
    AssertSuccess(await ExecuteAsync(customerApproval, "accept", new CashCollectionApprovalModel(approval.Id)), "customer accepts wallet shortfall");
    AssertEqual("cashShortfallRequested", realtime.CashApprovals[0].Change, "cash shortfall request realtime event");
    AssertEqual("cashShortfallApproved", realtime.CashApprovals[1].Change, "cash shortfall decision realtime event");
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(shortageRide.Id, 900m, "YER", null, "cash-shortage-complete")), "cash shortage after approval");
    db.ChangeTracker.Clear();
    AssertEqual(2_000m, await WalletBalanceAsync(db, fixture.CustomerId), "customer wallet after cash shortage");
    AssertEqual(2, realtime.Payments.Count, "cash collections publish one realtime event each");
}

static async Task AdministrativeApprovalSettlesPaidCashCancellationAsync(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    Fixture fixture)
{
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    var customerCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db));
    var driverCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    var adminCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.AdminId), RideAccounting(db));

    var walletRefundRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 50m);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(walletRefundRide.Id, 1_000m, "YER", null, "admin-cancel-wallet-cash")), "cash payment before administrative wallet refund");
    var balanceBeforeWalletRefund = await WalletBalanceAsync(db, fixture.CustomerId);
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(
        RideId: walletRefundRide.Id, Reason: "تغيرت خطط العميل", RequestedRefundMethod: CashCancellationRefundMethod.CreditCustomerWallet)), "customer asks cancellation with wallet refund");
    var walletRequest = await db.RideCancellationRequests.SingleAsync(x => x.RideId == walletRefundRide.Id);
    AssertSuccess(await ExecuteAsync(driverCancellation, "accept", new RideCancellationRequestModel(Id: walletRequest.Id)), "driver accepts paid cancellation");
    db.ChangeTracker.Clear();
    AssertEqual(RideCancellationStatus.AdminReviewPending, await db.RideCancellationRequests.Where(x => x.Id == walletRequest.Id).Select(x => x.Status).SingleAsync(), "paid cancellation awaits administration");
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: walletRequest.Id, Note: "اعتماد الاسترداد إلى المحفظة")), "admin settles cash cancellation to wallet");
    db.ChangeTracker.Clear();
    AssertEqual(balanceBeforeWalletRefund + 950m, await WalletBalanceAsync(db, fixture.CustomerId), "administrative decision credits the wallet with the refund");
    AssertEqual(RideStatus.Cancelled, await db.Rides.Where(x => x.Id == walletRefundRide.Id).Select(x => x.Status).SingleAsync(), "administrative decision cancels paid ride");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == walletRefundRide.Id && x.Provider == "CashRefundToCustomerWallet" && x.Status == PaymentStatus.Refunded), "administrative wallet refund creates immutable payment record");
    AssertEqual(-950m, await db.DriverSettlements.Where(x => x.PaymentReference != null && x.PaymentReference.StartsWith("cash-cancellation:payment:") && x.DriverId == fixture.DriverId).OrderByDescending(x => x.Id).Select(x => x.NetPayable).FirstAsync(), "administrative wallet refund creates driver receivable");
    var originalWalletPaymentId = await db.PaymentTransactions.Where(x => x.RideId == walletRefundRide.Id && x.Provider == "Cash" && x.Status == PaymentStatus.Paid).Select(x => x.Id).SingleAsync();
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.SourceId == $"payment:{originalWalletPaymentId}:wallet"), "administrative wallet refund posts one reversal journal");
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: walletRequest.Id)), "repeated administrative approval is harmless");
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.SourceId == $"payment:{originalWalletPaymentId}:wallet"), "repeated administrative approval does not duplicate journal");

    var directReturnRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 50m);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(directReturnRide.Id, 1_000m, "YER", null, "admin-cancel-direct-cash")), "cash payment before direct return");
    var balanceBeforeDirectReturn = await WalletBalanceAsync(db, fixture.CustomerId);
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(
        RideId: directReturnRide.Id, Reason: "لم تعد الرحلة مطلوبة", RequestedRefundMethod: CashCancellationRefundMethod.ReturnFromDriver)), "customer asks direct cash return");
    var directRequest = await db.RideCancellationRequests.SingleAsync(x => x.RideId == directReturnRide.Id);
    AssertSuccess(await ExecuteAsync(driverCancellation, "accept", new RideCancellationRequestModel(Id: directRequest.Id)), "driver accepts direct-return cancellation");
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: directRequest.Id)), "admin settles direct cash return");
    db.ChangeTracker.Clear();
    AssertEqual(balanceBeforeDirectReturn, await WalletBalanceAsync(db, fixture.CustomerId), "direct cash return does not change customer wallet");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == directReturnRide.Id && x.Provider == "CashReturnFromDriver" && x.Status == PaymentStatus.Refunded), "direct return creates immutable payment record");
    var originalDirectPaymentId = await db.PaymentTransactions.Where(x => x.RideId == directReturnRide.Id && x.Provider == "Cash" && x.Status == PaymentStatus.Paid).Select(x => x.Id).SingleAsync();
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.SourceId == $"payment:{originalDirectPaymentId}:driver-cash"), "direct return posts one reversal journal");

    // Wallet payment is currently accepted at trip completion. Arrange a
    // historical paid ride that is subsequently stopped while in progress so
    // the cancellation settlement branch remains covered independently from
    // the payment-timing workflow.
    var walletPaymentRide = await AddRideAsync(db, fixture, RideStatus.Completed, 900m, 100m, 50m);
    var walletPayment = new Payment(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db));
    var customerBeforeWalletCancellation = await WalletBalanceAsync(db, fixture.CustomerId);
    var driverBeforeWalletCancellation = await WalletBalanceAsync(db, fixture.DriverId);
    AssertSuccess(await ExecuteAsync(walletPayment, "add", new PaymentModel(
        RideId: walletPaymentRide.Id, Amount: 1_000m, Currency: "YER", Provider: "YemenDriveWallet", Status: PaymentStatus.Paid,
        IdempotencyKey: "admin-cancel-yemen-drive-wallet")), "wallet payment before administrative cancellation");
    walletPaymentRide.Status = RideStatus.InProgress;
    walletPaymentRide.CompletedAtUtc = null;
    await db.SaveChangesAsync();
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(
        RideId: walletPaymentRide.Id, Reason: "تغيرت خطط العميل بعد الدفع")), "customer asks cancellation after wallet payment");
    var walletPaymentRequest = await db.RideCancellationRequests.SingleAsync(x => x.RideId == walletPaymentRide.Id);
    AssertSuccess(await ExecuteAsync(driverCancellation, "accept", new RideCancellationRequestModel(Id: walletPaymentRequest.Id)), "driver accepts wallet-paid cancellation");
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: walletPaymentRequest.Id)), "admin settles Yemen Drive wallet cancellation");
    db.ChangeTracker.Clear();
    AssertEqual(customerBeforeWalletCancellation - 50m, await WalletBalanceAsync(db, fixture.CustomerId), "wallet payment cancellation returns the payment less cancellation fee");
    AssertEqual(driverBeforeWalletCancellation, await WalletBalanceAsync(db, fixture.DriverId), "wallet payment cancellation reverses the driver's credited share");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == walletPaymentRide.Id && x.Provider == "YemenDriveWallet" && x.Status == PaymentStatus.Refunded), "wallet payment cancellation creates refund record");
    var originalYemenDriveWalletPaymentId = await db.PaymentTransactions.Where(x => x.RideId == walletPaymentRide.Id && x.Provider == "YemenDriveWallet" && x.Status == PaymentStatus.Paid).Select(x => x.Id).SingleAsync();
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.SourceId == $"payment:{originalYemenDriveWalletPaymentId}:wallet"), "wallet payment cancellation posts one reversal journal");
}

static async Task CashPaidRideCancellationSupportsBothRefundChoicesAsync(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    Fixture fixture)
{
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    var customerCancellation = new RideService(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db));

    var directReturnRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 50m);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(directReturnRide.Id, 1_000m, "YER", null, "cash-cancel-direct")), "cash payment before direct return cancellation");
    var balanceBeforeDirectReturn = await WalletBalanceAsync(db, fixture.CustomerId);
    AssertSuccess(await ExecuteAsync(customerCancellation, "cancel", new RideModel(
        Id: directReturnRide.Id,
        CashCancellationRefundMethod: CashCancellationRefundMethod.ReturnFromDriver)), "cash cancellation by direct driver return");
    db.ChangeTracker.Clear();
    AssertEqual(balanceBeforeDirectReturn, await WalletBalanceAsync(db, fixture.CustomerId), "direct cash return must not change customer wallet");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == directReturnRide.Id && x.Status == PaymentStatus.Refunded && x.Provider == "CashReturnFromDriver"), "direct cash return creates immutable refund record");
    var directPaymentId = await db.PaymentTransactions.Where(x => x.RideId == directReturnRide.Id && x.Status == PaymentStatus.Paid)
        .Select(x => x.Id).SingleAsync();
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.SourceId == $"payment:{directPaymentId}:driver-cash"), "direct cash return creates one journal reversal");
    AssertEqual(0, await db.DriverSettlements.CountAsync(x => x.PaymentReference != null && x.PaymentReference.Contains($"cash-cancellation:payment:") && x.DriverId == fixture.DriverId), "direct cash return creates no additional driver debt");
    AssertEqual(RideStatus.Cancelled, await db.Rides.Where(x => x.Id == directReturnRide.Id).Select(x => x.Status).SingleAsync(), "direct return ride cancelled");
    Assert(!(await ExecuteAsync(customerCancellation, "cancel", new RideModel(
        Id: directReturnRide.Id,
        CashCancellationRefundMethod: CashCancellationRefundMethod.ReturnFromDriver))).Success, "direct cancellation cannot be repeated");

    var walletRefundRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 50m);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(walletRefundRide.Id, 1_000m, "YER", null, "cash-cancel-wallet")), "cash payment before wallet refund cancellation");
    var balanceBeforeWalletRefund = await WalletBalanceAsync(db, fixture.CustomerId);
    AssertSuccess(await ExecuteAsync(customerCancellation, "cancel", new RideModel(
        Id: walletRefundRide.Id,
        CashCancellationRefundMethod: CashCancellationRefundMethod.CreditCustomerWallet)), "cash cancellation to customer wallet");
    db.ChangeTracker.Clear();
    const decimal expectedRefund = 950m;
    AssertEqual(balanceBeforeWalletRefund + expectedRefund, await WalletBalanceAsync(db, fixture.CustomerId), "wallet cancellation credits the customer after cancellation fee");
    AssertEqual(1, await db.WalletTransactions.CountAsync(x => x.RideId == walletRefundRide.Id && x.Type == WalletTransactionType.Refund && x.Amount == expectedRefund), "wallet cancellation creates refund wallet movement");
    var cancellationDebt = await db.DriverSettlements.SingleAsync(x => x.PaymentReference != null && x.PaymentReference.StartsWith("cash-cancellation:payment:") && x.DriverId == fixture.DriverId);
    AssertEqual(-expectedRefund, cancellationDebt.NetPayable, "wallet refund creates separate driver debt");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == walletRefundRide.Id && x.Status == PaymentStatus.Refunded && x.Provider == "CashRefundToCustomerWallet"), "wallet cancellation creates immutable refund record");
    Assert(await db.JournalEntries.AnyAsync(x => x.SourceType == "RideCancellation" && x.SourceId!.EndsWith(":wallet")), "wallet cancellation creates journal reversal");
    Assert(await db.Notifications.AnyAsync(x => x.UserId == fixture.DriverId && x.Body.Contains("مديونية مستقلة")), "driver receives wallet-refund debt notification");
}

static async Task CustomerCashPaymentRequiresDriverConfirmationAsync(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    Fixture fixture)
{
    var ride = await AddRideAsync(db, fixture, RideStatus.Completed, 900m, 100m, 0m);
    var realtime = new RecordingRideRealtimePublisher();
    var customerRequest = new CashPaymentRequestService(db, configuration, new TestCurrentUser(fixture.CustomerId), realtime);
    var driverRequest = new CashPaymentRequestService(db, configuration, new TestCurrentUser(fixture.DriverId), realtime);
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db), realtime);

    AssertSuccess(await ExecuteAsync(customerRequest, "add", new CashPaymentRequestModel(RideId: ride.Id, IdempotencyKey: "cash-customer-reject")), "customer asks for cash confirmation");
    var rejected = await db.CashPaymentRequests.OrderByDescending(x => x.Id).FirstAsync(x => x.RideId == ride.Id);
    AssertSuccess(await ExecuteAsync(driverRequest, "reject", new CashPaymentRequestModel(Id: rejected.Id)), "driver rejects unreceived cash");
    AssertEqual(CashPaymentRequestStatus.DriverRejected, await db.CashPaymentRequests.Where(x => x.Id == rejected.Id).Select(x => x.Status).SingleAsync(), "rejected cash request is recorded");
    Assert(await db.Notifications.AnyAsync(x => x.UserId == fixture.CustomerId && x.Body.Contains("لم يستلم")), "customer is notified of cash rejection");

    AssertSuccess(await ExecuteAsync(customerRequest, "add", new CashPaymentRequestModel(RideId: ride.Id, IdempotencyKey: "cash-customer-confirm")), "customer retries cash confirmation");
    var confirmed = await db.CashPaymentRequests.OrderByDescending(x => x.Id).FirstAsync(x => x.RideId == ride.Id);
    AssertSuccess(await ExecuteAsync(driverRequest, "accept", new CashPaymentRequestModel(Id: confirmed.Id)), "driver confirms received cash");
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 1_000m, "YER", null, "cash-customer-confirmed")), "driver records confirmed customer cash");
    AssertEqual("cashPaymentRequested", realtime.Payments[0].Change, "cash request realtime event");
    AssertEqual("cashPaymentRejected", realtime.Payments[1].Change, "cash rejection realtime event");
    AssertEqual("cashPaymentRequested", realtime.Payments[2].Change, "cash retry realtime event");
    AssertEqual("cashPaymentConfirmed", realtime.Payments[3].Change, "cash confirmation realtime event");
    AssertEqual("cashPaymentRecorded", realtime.Payments[4].Change, "cash collection realtime event");
    db.ChangeTracker.Clear();
    AssertEqual(CashPaymentRequestStatus.Collected, await db.CashPaymentRequests.Where(x => x.Id == confirmed.Id).Select(x => x.Status).SingleAsync(), "cash request becomes collected after payment record");
    AssertEqual(RideStatus.Completed, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "cash posting does not alter completed trip status");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id && x.Provider == "Cash" && x.Status == PaymentStatus.Paid), "confirmed cash creates exactly one payment");
}

static async Task InsufficientCashShortageRollsBackAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var wallet = await db.Wallets.SingleAsync(x => x.UserId == fixture.CustomerId);
    wallet.Balance = 50m;
    await db.SaveChangesAsync();
    var ride = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    var realtime = new RecordingRideRealtimePublisher();
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db), realtime);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 800m, "YER", null, "cash-insufficient")), "insufficient shortage request");
    db.ChangeTracker.Clear();
    AssertEqual(0, await db.CashCollectionApprovals.CountAsync(x => x.RideId == ride.Id), "insufficient shortage creates no approval request");
    Assert(await db.Notifications.AnyAsync(x => x.UserId == fixture.CustomerId && x.Body.Contains("لا يكفي")), "customer receives insufficient-wallet notification");
    AssertEqual(50m, await WalletBalanceAsync(db, fixture.CustomerId), "wallet unchanged after rejected cash shortage");
    AssertEqual(0, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id), "no payment after rejected cash shortage");
    AssertEqual(0, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCashPayment" && x.SourceId == $"cash:ride:{ride.Id}:driver:{fixture.DriverId}"), "no journal after rejected cash shortage");
    AssertEqual(RideStatus.InProgress, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "ride remains in progress after rejected cash shortage");
    AssertEqual("cashCollectionInsufficientBalance", realtime.Payments.Single().Change, "cash shortfall rejection realtime event");
}

static async Task<YemenDrive.Database.Entities.Ride> AddRideAsync(YemenDriveDbContext db, Fixture fixture, RideStatus status, decimal fare, decimal serviceFee, decimal cancellationFee)
{
    var ride = new YemenDrive.Database.Entities.Ride
    {
        CustomerId = fixture.CustomerId, DriverId = fixture.DriverId, ServiceKindId = fixture.ServiceKindId,
        ServiceCatalogItemId = fixture.ServiceCatalogItemId, Status = status, PickupAddress = "A", DestinationAddress = "B",
        CustomerPrice = fare, ServiceFee = serviceFee, CancellationFee = cancellationFee, TotalAmount = fare + serviceFee,
        DriverCommissionAmount = 50m, DriverShare = fare - 50m, PlatformShare = serviceFee + 50m,
        StartedAtUtc = status is RideStatus.InProgress or RideStatus.Completed ? DateTime.UtcNow : null,
        CompletedAtUtc = status == RideStatus.Completed ? DateTime.UtcNow : null
    };
    db.Rides.Add(ride);
    await db.SaveChangesAsync();
    return ride;
}

static Task<ApiResult> ExecuteAsync<T>(YemenDrive.Services.Core.ModelService<T> service, string operation, T model) where T : class =>
    service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);

static Task<decimal> WalletBalanceAsync(YemenDriveDbContext db, int userId) =>
    db.Wallets.Where(x => x.UserId == userId).Select(x => x.Balance).SingleAsync();

static RideAccountingPostingService RideAccounting(YemenDriveDbContext db) =>
    new(db, new AccountingPostingService(db));

static void AssertSuccess(ApiResult result, string name) => Assert(result.Success, $"{name} failed: {result.Code}");
static void AssertEqual<T>(T expected, T actual, string name) where T : notnull => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"{name}: expected {expected}, got {actual}");
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed record Fixture(int CustomerId, int DriverId, int AdminId, int ServiceKindId, int ServiceCatalogItemId, int CustomerWalletId, int DriverWalletId);
file sealed class RecordingRideRealtimePublisher : IRideRealtimePublisher
{
    public List<RideAvailableRecipient> AvailableRecipients { get; } = [];
    public int? OfferReceivedCustomerId { get; private set; }
    public int? RideUnavailableCustomerId { get; private set; }
    public int? RideUnavailableRideId { get; private set; }
    public RideOfferRealtimeEvent? ReceivedOffer { get; private set; }
    public List<RideOfferRealtimeEvent> UpdatedOffers { get; } = [];
    public List<RideStatusRealtimeEvent> RideStatuses { get; } = [];
    public List<DriverLocationRealtimeEvent> DriverLocations { get; } = [];
    public List<RideMessageRealtimeEvent> RideMessages { get; } = [];
    public List<PaymentRealtimeEvent> Payments { get; } = [];
    public List<PaymentRealtimeEvent> CashApprovals { get; } = [];
    public List<NotificationRealtimeEvent> Notifications { get; } = [];
    public List<SafetyIncidentRealtimeEvent> SafetyIncidents { get; } = [];
    public List<SafetyRecordingRealtimeEvent> SafetyRecordings { get; } = [];

    public Task PublishRideAvailableAsync(
        IReadOnlyCollection<RideAvailableRecipient> recipients,
        CancellationToken cancellationToken)
    {
        AvailableRecipients.AddRange(recipients);
        return Task.CompletedTask;
    }

    public Task PublishRideUnavailableAsync(
        int rideId,
        int customerUserId,
        IReadOnlyCollection<int> driverUserIds,
        CancellationToken cancellationToken)
    {
        RideUnavailableRideId = rideId;
        RideUnavailableCustomerId = customerUserId;
        return Task.CompletedTask;
    }

    public Task PublishOfferReceivedAsync(
        int customerUserId,
        RideOfferRealtimeEvent offer,
        CancellationToken cancellationToken)
    {
        OfferReceivedCustomerId = customerUserId;
        ReceivedOffer = offer;
        return Task.CompletedTask;
    }

    public Task PublishOfferUpdatesAsync(
        int customerUserId,
        IReadOnlyCollection<RideOfferRealtimeEvent> offers,
        CancellationToken cancellationToken)
    {
        UpdatedOffers.AddRange(offers);
        return Task.CompletedTask;
    }

    public Task PublishRideStatusUpdatedAsync(
        RideStatusRealtimeEvent rideStatus,
        CancellationToken cancellationToken)
    {
        RideStatuses.Add(rideStatus);
        return Task.CompletedTask;
    }

    public Task PublishDriverLocationAsync(
        DriverLocationRealtimeEvent location,
        CancellationToken cancellationToken)
    {
        DriverLocations.Add(location);
        return Task.CompletedTask;
    }

    public Task PublishRideMessageCreatedAsync(
        RideMessageRealtimeEvent message,
        CancellationToken cancellationToken)
    {
        RideMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task PublishPaymentUpdatedAsync(
        IReadOnlyCollection<int> userIds,
        PaymentRealtimeEvent payment,
        CancellationToken cancellationToken)
    {
        Payments.Add(payment);
        return Task.CompletedTask;
    }

    public Task PublishCashApprovalUpdatedAsync(
        IReadOnlyCollection<int> userIds,
        PaymentRealtimeEvent approval,
        CancellationToken cancellationToken)
    {
        CashApprovals.Add(approval);
        return Task.CompletedTask;
    }

    public Task PublishNotificationsCreatedAsync(
        IReadOnlyCollection<NotificationRealtimeEvent> notifications,
        CancellationToken cancellationToken)
    {
        Notifications.AddRange(notifications);
        return Task.CompletedTask;
    }

    public Task PublishSafetyIncidentCreatedAsync(
        SafetyIncidentRealtimeEvent incident,
        CancellationToken cancellationToken)
    {
        SafetyIncidents.Add(incident);
        return Task.CompletedTask;
    }

    public Task PublishSafetyIncidentUpdatedAsync(
        SafetyIncidentRealtimeEvent incident,
        CancellationToken cancellationToken)
    {
        SafetyIncidents.Add(incident);
        return Task.CompletedTask;
    }

    public Task PublishSafetyRecordingAvailableAsync(
        SafetyRecordingRealtimeEvent recording,
        CancellationToken cancellationToken)
    {
        SafetyRecordings.Add(recording);
        return Task.CompletedTask;
    }

    public Task PublishRideShareRevokedAsync(
        int shareId,
        int rideId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class TestCurrentUser(int userId) : ICurrentUserContext
{
    public int? UserId => userId;
}

file sealed class SignalRTestConnection(ClientWebSocket socket) : IAsyncDisposable
{
    private const char RecordSeparator = '\u001e';
    private readonly StringBuilder _pending = new();
    private readonly Queue<JsonElement> _queuedEvents = new();
    private int _nextInvocationId;

    public async Task HandshakeAsync()
    {
        await SendFrameAsync(new { protocol = "json", version = 1 });
        using var response = await ReadFrameAsync();
        if (response.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"SignalR handshake failed: {error.GetString()}");
    }

    public async Task<string?> InvokeErrorAsync(string method, params object[] arguments)
    {
        var invocationId = (++_nextInvocationId).ToString();
        await SendFrameAsync(new
        {
            type = 1,
            invocationId,
            target = method,
            arguments
        });

        while (true)
        {
            using var response = await ReadFrameAsync();
            var root = response.RootElement;
            if (root.TryGetProperty("type", out var eventType) && eventType.GetInt32() == 1)
            {
                _queuedEvents.Enqueue(root.Clone());
                continue;
            }
            if (!root.TryGetProperty("type", out var type) || type.GetInt32() != 3 ||
                !root.TryGetProperty("invocationId", out var responseId) ||
                responseId.GetString() != invocationId)
                continue;
            return root.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
    }

    public async Task<bool> ReceiveEventAsync(string target)
    {
        while (_queuedEvents.Count > 0)
        {
            if (HasTarget(_queuedEvents.Dequeue(), target)) return true;
        }
        while (true)
        {
            using var response = await ReadFrameAsync();
            var root = response.RootElement;
            if (HasTarget(root, target)) return true;
            if (root.TryGetProperty("type", out var type) && type.GetInt32() == 1)
                _queuedEvents.Enqueue(root.Clone());
        }
    }

    private static bool HasTarget(JsonElement root, string target) =>
        root.TryGetProperty("type", out var type) && type.GetInt32() == 1 &&
        root.TryGetProperty("target", out var receivedTarget) &&
        string.Equals(receivedTarget.GetString(), target, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "integration test complete",
                    timeout.Token);
            }
            catch (WebSocketException)
            {
                socket.Abort();
            }
            catch (OperationCanceledException)
            {
                socket.Abort();
            }
        }
        socket.Dispose();
    }

    private async Task SendFrameAsync<T>(T message)
    {
        var frame = JsonSerializer.Serialize(message) + RecordSeparator;
        var bytes = Encoding.UTF8.GetBytes(frame);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private async Task<JsonDocument> ReadFrameAsync()
    {
        while (true)
        {
            var separator = _pending.ToString().IndexOf(RecordSeparator);
            if (separator >= 0)
            {
                var frame = _pending.ToString(0, separator);
                _pending.Remove(0, separator + 1);
                return JsonDocument.Parse(frame);
            }

            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("SignalR closed before completing the expected message.");
            _pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
    }
}
