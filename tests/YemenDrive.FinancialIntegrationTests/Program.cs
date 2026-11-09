using System.Net;
using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
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

RealtimeScenarioMode.Enabled = args.Contains("--journal");
Console.WriteLine($"MODE: durable journal = {RealtimeScenarioMode.Enabled}");
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
    await using var db = new YemenDriveDbContext(options, RealtimeScenarioMode.Settings);
    await db.Database.MigrateAsync();
    var fixture = await SeedAsync(db);
    db.AdministratorPermissions.Add(new() { UserId = fixture.AdminId, Permission = "*" }); await db.SaveChangesAsync();
    if (args.Contains("--offer-acceptance-race"))
    {
        await OfferAcceptanceConcurrencyScenarios.RunAsync(db, configuration, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
        return;
    }
    if (args.Contains("--repeat-offer-acceptance"))
    {
        await RepeatOfferAcceptanceScenarios.RunAsync(db, configuration, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
        return;
    }
    if (args.Contains("--prestart"))
    {
        await new FinancialAccountProvisioningService(db).ProvisionExistingAsync(CancellationToken.None);
        await PrestartPaymentScenarios.RunAsync(db, configuration, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId,
            forceDebitWinner: args.Contains("--prestart-debit-winner"));
        return;
    }
    if (args.Contains("--search-cancellation"))
    {
        await SearchRestartAndDirectCancellationRemainConsistentAsync(db, configuration, fixture);
        await CancellationSnapshotIsOptionalAndPrivateAsync(db, configuration, fixture);
        return;
    }
    if (args.Contains("--waiting-control"))
    {
        await WaitingControlScenarios.RunAsync(db, configuration, fixture.CustomerId, fixture.DriverId, fixture.AdminId,
            fixture.ServiceKindId, fixture.ServiceCatalogItemId);
        return;
    }

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
    // Earlier financial scenarios complete rides; finish their passenger reviews
    // before exercising the independent new-booking contract.
    var ratingService = new YemenDrive.Application.Drivers.DriverRating(db, configuration, new TestCurrentUser(fixture.CustomerId));
    foreach (var activeFixture in await db.Rides.AsNoTracking().Where(x => x.DriverId == fixture.DriverId && x.Status == RideStatus.InProgress && x.ServiceEndedAtUtc == null).ToListAsync())
        AssertSuccess(await new RideServiceControl(db, configuration, new TestCurrentUser(fixture.DriverId)).ExecuteAsync("finish", JsonSerializer.SerializeToElement(new RideServiceModel(activeFixture.Id, "انتهى النقل في سيناريو الاختبار عند نقطة آمنة")), default), "finish earlier fixture transport");
    foreach (var completed in await db.Rides.AsNoTracking().Where(x => x.CustomerId == fixture.CustomerId && x.Status == RideStatus.Completed).ToListAsync())
        AssertSuccess(await ratingService.ExecuteAsync("add", JsonSerializer.SerializeToElement(new YemenDrive.Application.Drivers.DriverRatingModel(RideId: completed.Id, Score: 5)), CancellationToken.None), "review completed fixture ride");
    // Earlier cancellation fixtures deliberately establish policy-backed fees.
    // Settle those verified test receipts before testing an unrelated new search.
    foreach (var debt in await db.RideFinancialCases.AsNoTracking().Where(x => x.CustomerId == fixture.CustomerId && x.Kind == "Receivable" && (x.Status == "Open" || x.Status == "Confirmed") && x.Amount > 0).ToListAsync())
        AssertSuccess(await ExecuteAsync(new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db)), "add", new DriverCashPaymentModel(debt.RideId, debt.Amount, "YER", IdempotencyKey: $"fixture-receivable-{debt.Id}")), "clear verified fixture receivable");
    await RideSearchAndOfferEventsArePublishedAfterPersistenceAsync(db, configuration, fixture);
    await PricingWaitingScenarios.RunAsync(db, configuration, fixture.CustomerId, fixture.DriverId, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
    await WaitingControlScenarios.RunAsync(db, configuration, fixture.CustomerId, fixture.DriverId, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
    await WaitingReportScenarios.RunAsync(db, configuration, fixture.CustomerId, fixture.DriverId, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
    await RouteChangeScenarios.RunAsync(db, configuration, fixture.CustomerId, fixture.DriverId, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId);

    await WorkflowAcceptanceScenarios.RunAsync(db, configuration, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
    await RepeatOfferAcceptanceScenarios.RunAsync(db, configuration, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
    await OfferAcceptanceConcurrencyScenarios.RunAsync(db, configuration, fixture.ServiceKindId, fixture.ServiceCatalogItemId);

    await WalletRideGuardScenarios.RunAsync(db, configuration, fixture.CustomerId, fixture.DriverId, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
    await ExternalWalletPaymentPostsOnceAndLocksOtherTendersAsync(db, configuration, fixture);
    await CustomerPaymentAccessChangesArePublishedAsync(db, configuration, fixture, options);
    await PrestartPaymentScenarios.RunAsync(db, configuration, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId);
    await CancellationRefundScenarios.RunAsync(db, configuration, fixture.AdminId, fixture.ServiceKindId, fixture.ServiceCatalogItemId);

    await ExpandedCancellationAndAccountingAsync(db, configuration, fixture);
    await SearchRestartAndDirectCancellationRemainConsistentAsync(db, configuration, fixture);
    await CancellationSnapshotIsOptionalAndPrivateAsync(db, configuration, fixture);
    Console.WriteLine("PASS: financial integration scenarios completed on an isolated temporary database.");
}
finally
{
    await using var cleanup = new YemenDriveDbContext(options);
    await cleanup.Database.EnsureDeletedAsync();
    if (File.Exists(settingsPath)) File.Delete(settingsPath);
}

static async Task CancellationSnapshotIsOptionalAndPrivateAsync(
    YemenDriveDbContext db, DatabaseConfigurationStore config, Fixture fixture)
{
    var customer = new User { PhoneNumber = "+967700126802", DisplayName = "Optional snapshot rider", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "+967700126803", DisplayName = "Optional snapshot driver", Role = UserRole.Driver };
    var ride = new YemenDrive.Database.Entities.Ride { Customer = customer, Driver = driver,
        ServiceKindId = fixture.ServiceKindId, ServiceCatalogItemId = fixture.ServiceCatalogItemId,
        Status = RideStatus.DriverAssigned };
    db.Rides.Add(ride);
    await db.SaveChangesAsync();
    foreach (var userId in new[] { customer.Id, driver.Id, fixture.AdminId })
    {
        var result = await ExecuteAsync(new RideCancellationRequestService(db, config,
            new TestCurrentUser(userId), RideAccounting(db)), "get", new RideCancellationRequestModel(RideId: ride.Id));
        Assert(result.Success && result.Data is null, "participant reads a normal ride without a cancellation snapshot");
    }
    var outsider = new RideCancellationRequestService(db, config, new TestCurrentUser(fixture.CustomerId), RideAccounting(db));
    var denied = await ExecuteAsync(outsider, "get", new RideCancellationRequestModel(RideId: ride.Id));
    Assert(!denied.Success && denied.Code == "ride_access_denied", "empty cancellation snapshot still enforces ride ownership");
    var missing = await ExecuteAsync(outsider, "get", new RideCancellationRequestModel(Id: int.MaxValue));
    Assert(!missing.Success && missing.Code == "cancellation_request_not_found", "explicit missing cancellation id remains an error");
    var missingRide = await ExecuteAsync(outsider, "get", new RideCancellationRequestModel(RideId: int.MaxValue));
    Assert(!missingRide.Success && missingRide.Code == "ride_not_found", "missing ride is not an empty cancellation snapshot");
    ride.Status = RideStatus.InProgress;
    ride.StartedAtUtc = DateTime.UtcNow.AddHours(-2);
    ride.ServiceEndedAtUtc = ride.StartedAtUtc.Value.AddSeconds(3600);
    for (var index = 0; index <= 3600; index++)
        db.LocationUpdates.Add(new LocationUpdate { RideId = ride.Id, ActorId = driver.Id,
            Latitude = 15.3 + index * .00001, Longitude = 44.2, Accuracy = 5,
            ObservedAtUtc = ride.StartedAtUtc.Value.AddSeconds(index) });
    db.LocationUpdates.AddRange(
        new LocationUpdate { RideId = ride.Id, ActorId = customer.Id, Latitude = 14, Longitude = 44, ObservedAtUtc = ride.StartedAtUtc.Value },
        new LocationUpdate { RideId = ride.Id, ActorId = driver.Id, Latitude = 14, Longitude = 44, ObservedAtUtc = ride.StartedAtUtc.Value.AddSeconds(-1) },
        new LocationUpdate { RideId = ride.Id, ActorId = driver.Id, Latitude = 14, Longitude = 44, ObservedAtUtc = ride.ServiceEndedAtUtc.Value.AddSeconds(1) });
    await db.SaveChangesAsync();
    var detailResult = await ExecuteAsync(new RideService(db, config, new TestCurrentUser(customer.Id), RideAccounting(db)), "get", new RideModel(Id: ride.Id));
    AssertSuccess(detailResult, "ride detail includes persisted travelled path");
    var detail = JsonSerializer.SerializeToElement(detailResult.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var samples = detail.GetProperty("driverRouteSamples").EnumerateArray().ToArray();
    Assert(samples.Length is > 1 and <= 602, "history snapshot is bounded without losing its beginning");
    Assert(samples[0].GetProperty("latitude").GetDouble() == 15.3 && samples[^1].GetProperty("latitude").GetDouble() == 15.336,
        "history retains first and last actual driver samples and excludes passenger/outside-trip observations");
    var times = samples.Select(x => x.GetProperty("observedAtUtc").GetDateTime()).ToArray();
    Assert(times.SequenceEqual(times.Order()) && times.All(x => x.Kind == DateTimeKind.Utc), "route samples stay ordered and UTC after SQL roundtrip");
    var otherDetail = await ExecuteAsync(new RideService(db, config, new TestCurrentUser(fixture.CustomerId), RideAccounting(db)), "get", new RideModel(Id: ride.Id));
    Assert(!otherDetail.Success && otherDetail.Code == "ride_access_denied", "route history remains private to the ride participants");
    var fault = new RouteHistoryReadFailure();
    var recoveryOptions = new DbContextOptionsBuilder<YemenDriveDbContext>()
        .UseSqlServer(db.Database.GetConnectionString()).AddInterceptors(fault).Options;
    await using (var recoveryDb = new YemenDriveDbContext(recoveryOptions))
    {
        var service = new RideService(recoveryDb, config, new TestCurrentUser(customer.Id), RideAccounting(recoveryDb));
        var lightweight = await ExecuteAsync(service, "get", new RideModel(Id: ride.Id, IncludeRouteHistory: false));
        AssertSuccess(lightweight, "lightweight ride snapshot omits optional route history");
        var lightweightJson = JsonSerializer.SerializeToElement(lightweight.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(!lightweightJson.GetProperty("driverRouteHistoryRequested").GetBoolean() &&
            !lightweightJson.GetProperty("driverRouteHistoryAvailable").GetBoolean() &&
            lightweightJson.GetProperty("driverRouteSamples").GetArrayLength() == 0 && fault.ReadAttempts == 0,
            "explicitly omitted archive executes no display-history query and advertises the omission");
        var degraded = await ExecuteAsync(service, "get", new RideModel(Id: ride.Id));
        AssertSuccess(degraded, "display archive timeout does not block the canonical ride snapshot");
        var degradedJson = JsonSerializer.SerializeToElement(degraded.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(degradedJson.GetProperty("driverRouteHistoryRequested").GetBoolean() && !degradedJson.GetProperty("driverRouteHistoryAvailable").GetBoolean() &&
            degradedJson.GetProperty("ride").GetProperty("id").GetInt32() == ride.Id,
            "archive failure is explicit while the current ride remains usable");
        var recovered = await ExecuteAsync(service, "get", new RideModel(Id: ride.Id, IncludeRouteHistory: true));
        AssertSuccess(recovered, "next refresh recovers the archived route automatically");
        var recoveredJson = JsonSerializer.SerializeToElement(recovered.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(recoveredJson.GetProperty("driverRouteHistoryAvailable").GetBoolean() &&
            recoveredJson.GetProperty("driverRouteSamples").GetArrayLength() > 1,
            "route history returns after a transient archive read failure");
    }
    Console.WriteLine("PASS: optional cancellation snapshots retain participant authorization and explicit-id errors.");
    Console.WriteLine("PASS: persisted route display history is bounded, ordered, private and spans destination changes.");
}

static async Task SearchRestartAndDirectCancellationRemainConsistentAsync(
    YemenDriveDbContext db, DatabaseConfigurationStore config, Fixture fixture)
{
    var customer = new User { PhoneNumber = "+967700126800", DisplayName = "Search cancellation rider", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "+967700126801", DisplayName = "Search cancellation driver", Role = UserRole.Driver };
    db.AddRange(customer, driver); await db.SaveChangesAsync();
    db.DriverProfiles.Add(new DriverProfile { UserId = driver.Id, ServiceKindId = fixture.ServiceKindId,
        ServiceCatalogItemId = fixture.ServiceCatalogItemId, IsAvailable = true, IsApproved = true,
        AcceptsNewRequests = true, ApprovedPassengerSeats = 4, VehicleModel = "Test", PlateNumber = "SEARCH" });
    db.DriverLiveLocations.Add(new DriverLiveLocation { DriverId = driver.Id, Latitude = 15.3694,
        Longitude = 44.1910, IsOnline = true, ObservedAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync();
    var publisher = new RecordingRideRealtimePublisher();
    var search = new RideSearchAction(db, config, new TestCurrentUser(customer.Id), publisher);
    var offers = new RideOfferService(db, config, new TestCurrentUser(driver.Id), publisher);
    var cancellation = new RideCancellationRequestService(db, config, new TestCurrentUser(customer.Id), RideAccounting(db), publisher);
    async Task<YemenDrive.Database.Entities.Ride> Trip(RideStatus status = RideStatus.Searching)
    {
        var trip = new YemenDrive.Database.Entities.Ride { CustomerId = customer.Id, ServiceKindId = fixture.ServiceKindId,
            ServiceCatalogItemId = fixture.ServiceCatalogItemId, Status = status, PickupLatitude = 15.3694,
            PickupLongitude = 44.1910, CustomerPrice = 1000, TotalAmount = 1100, ServiceFee = 100,
            PickupAddress = "Search pickup", DestinationAddress = "Search destination", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3) };
        db.Rides.Add(trip); await db.SaveChangesAsync(); return trip;
    }
    async Task<YemenDrive.Database.Entities.RideOffer> Offer(YemenDrive.Database.Entities.Ride trip)
    {
        AssertSuccess(await ExecuteAsync(offers, "add", new RideOfferModel(RideId: trip.Id, Amount: 1000)), "new search offer");
        return await db.RideOffers.SingleAsync(x => x.RideId == trip.Id && x.Status == OfferStatus.Pending);
    }

    var open = await Trip();
    var originalCreatedAt = open.CreatedAtUtc;
    AssertSuccess(await ExecuteAsync(search, "update", new RideSearchActionModel(open.Id)), "no-offer retry sends fresh invitations while search remains open");
    var first = await Offer(open);
    AssertSuccess(await ExecuteAsync(new YemenDrive.Application.Rides.RideSearchView(db, config, new TestCurrentUser(driver.Id)),
        "update", new RideSearchViewModel(open.Id, 1000)), "record viewer before next round");
    AssertSuccess(await ExecuteAsync(search, "update", new RideSearchActionModel(open.Id)), "explicit retry works while API search is still negotiating");
    Assert(first.Status == OfferStatus.Expired && open.Status == RideStatus.Searching, "new round expires previous offer and stays on same ride");
    AssertEqual(originalCreatedAt, open.CreatedAtUtc, "retry preserves request age and search-radius policy");
    AssertEqual(0, await db.RideSearchViews.CountAsync(x => x.RideId == open.Id), "new round clears old viewers");
    var snapshot = publisher.AvailableRecipients.Last(x => x.UserId == driver.Id).Ride;
    AssertEqual((int)OfferStatus.Expired, Convert.ToInt32(snapshot["driverOfferStatus"]), "new invitation snapshot never advertises an expired offer as pending");
    Assert(publisher.UpdatedOffers.Any(x => x.Id == first.Id && x.Status == OfferStatus.Expired), "old offer expiration is broadcast");
    var repeated = await Offer(open);
    var notificationCount = await db.Notifications.CountAsync();
    AssertSuccess(await ExecuteAsync(search, "update", new RideSearchActionModel(open.Id, CustomerPrice: 1000)), "lost same-price response can be retried after a new offer");
    Assert(repeated.Status == OfferStatus.Pending && open.Status == RideStatus.Negotiating, "same-price retry preserves newly arrived offer");
    AssertEqual(notificationCount, await db.Notifications.CountAsync(), "same-price retry sends no duplicate invitations");
    AssertSuccess(await ExecuteAsync(search, "update", new RideSearchActionModel(open.Id)), "another explicit retry begins another round");
    Assert(repeated.Status == OfferStatus.Expired && await db.Notifications.CountAsync() > notificationCount, "explicit retry expires and rebroadcasts");
    AssertSuccess(await ExecuteAsync(search, "cancel", new RideSearchActionModel(open.Id, Exhausted: true)), "legacy no-offer timeout closes search");
    Assert(open.ServiceEndedAtUtc is null, "timeout does not become a formal cancellation");
    AssertSuccess(await ExecuteAsync(search, "update", new RideSearchActionModel(open.Id)), "legacy timeout can reopen");
    AssertSuccess(await ExecuteAsync(search, "cancel", new RideSearchActionModel(open.Id, Reason: "لم أعد بحاجة إلى الرحلة")), "explicit cancellation is final");
    Assert(open.ServiceEndedAtUtc is not null && open.FinancialAmountDue == 0, "formal unpaid search cancellation ends service with zero due");
    Assert(!(await ExecuteAsync(search, "update", new RideSearchActionModel(open.Id))).Success, "formal cancellation cannot reopen");
    AssertSuccess(await ExecuteAsync(search, "cancel", new RideSearchActionModel(open.Id, Reason: "لم أعد بحاجة إلى الرحلة")), "formal cancellation replay remains idempotent");
    AssertEqual(1, await db.RideCancellationRequests.CountAsync(x => x.RideId == open.Id), "formal cancellation replay creates no second case");

    foreach (var status in new[] { RideStatus.Searching, RideStatus.Negotiating })
    {
        var trip = await Trip(status);
        YemenDrive.Database.Entities.RideOffer? previous = status == RideStatus.Negotiating ? await Offer(trip) : null;
        var result = await ExecuteAsync(cancellation, "add", new RideCancellationRequestModel(RideId: trip.Id, Reason: "تغير موعد الرحلة"));
        AssertSuccess(result, "generic endpoint directly cancels unpaid unassigned search");
        var json = JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = await db.RideCancellationRequests.SingleAsync(x => x.RideId == trip.Id);
        Assert(request.Status == RideCancellationStatus.AdminApproved && request.AssignedAdminId is null && request.ReviewDueAtUtc is null,
            "safe unassigned cancellation does not create an approval task");
        Assert(trip.Status == RideStatus.Cancelled && trip.ServiceEndedAtUtc is not null && trip.FinancialAmountDue == 0,
            "unassigned transport is finally cancelled without a debt");
        Assert(json.GetProperty("status").GetInt32() == 3 && json.GetProperty("rideStatus").GetInt32() == 7 && !json.GetProperty("canDecide").GetBoolean(),
            "direct cancellation response has existing numeric status and no decision gate");
        Assert(!await db.PaymentTransactions.AnyAsync(x => x.RideId == trip.Id) && !await db.JournalLines.AnyAsync(x => x.RideId == trip.Id),
            "unpaid cancellation makes no financial movement");
        Assert(previous is null || previous.Status == OfferStatus.Expired, "generic cancellation expires pending offers");
        AssertEqual(trip.Id, publisher.RideUnavailableRideId!.Value, "generic cancellation broadcasts unavailable ride");
        Assert(!(await ExecuteAsync(search, "update", new RideSearchActionModel(trip.Id))).Success, "generic final cancellation cannot reopen");
    }

    var paid = await Trip();
    db.PaymentTransactions.Add(new PaymentTransaction { RideId = paid.Id, UserId = customer.Id, Amount = 100,
        Provider = "IsolatedGuardFixture", Status = PaymentStatus.Paid });
    await db.SaveChangesAsync();
    Assert(!(await ExecuteAsync(search, "cancel", new RideSearchActionModel(paid.Id, Reason: "إلغاء مدفوع"))).Success,
        "simple search cancellation cannot bypass paid refund handling");
    Assert(!(await ExecuteAsync(search, "update", new RideSearchActionModel(paid.Id))).Success,
        "search restart cannot repurpose a paid ride");
    AssertSuccess(await ExecuteAsync(cancellation, "add", new RideCancellationRequestModel(RideId: paid.Id, Reason: "إلغاء مدفوع")),
        "paid unassigned ride uses review workflow");
    Assert((await db.RideCancellationRequests.SingleAsync(x => x.RideId == paid.Id)).Status == RideCancellationStatus.AdminReviewPending,
        "unassigned paid cancellation retains administrator financial review");
    var paymentMethod = new YemenDrive.Database.Entities.PaymentMethod { Code = "search-guard-wallet", NameAr = "اختبار",
        ProviderCode = "Floosak", Kind = PaymentMethodKind.ExternalWallet };
    db.PaymentMethods.Add(paymentMethod); await db.SaveChangesAsync();
    var pending = await Trip();
    var operation = new PaymentWalletOperation { RideId = pending.Id, CustomerId = customer.Id, PaymentMethodId = paymentMethod.Id,
        RequestId = Guid.NewGuid().ToString("N"), ProviderCode = "Floosak", Status = "Processing", Stage = "Complete", Amount = 100, IsOpen = true };
    db.PaymentWalletOperations.Add(operation); await db.SaveChangesAsync();
    Assert(!(await ExecuteAsync(search, "cancel", new RideSearchActionModel(pending.Id, Exhausted: true))).Success,
        "search timeout cannot bypass uncertain payment handling");
    AssertSuccess(await ExecuteAsync(cancellation, "add", new RideCancellationRequestModel(RideId: pending.Id, Reason: "إلغاء أثناء تحقق الدفع")),
        "uncertain payment uses formal cancellation");
    Assert(operation.IsOpen && (await db.RideCancellationRequests.SingleAsync(x => x.RideId == pending.Id)).Status == RideCancellationStatus.AdminReviewPending,
        "in-flight debit remains linked to the financial review");
    Console.WriteLine("PASS: explicit search retry, stale-offer snapshots, final cancellation guards, direct unpaid cancellation and paid/uncertain review.");
}

static async Task ExpandedCancellationAndAccountingAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, Fixture fixture)
{
    var accounting = RideAccounting(db);
    var publisher = new RecordingRideRealtimePublisher();
    var rider = new RideCancellationRequestService(db, config, new TestCurrentUser(fixture.CustomerId), accounting, publisher);
    var driver = new RideCancellationRequestService(db, config, new TestCurrentUser(fixture.DriverId), accounting, publisher);
    var admin = new RideCancellationRequestService(db, config, new TestCurrentUser(fixture.AdminId), accounting, publisher);
    foreach (var status in new[] { RideStatus.DriverAssigned, RideStatus.DriverEnRoute })
    {
        var trip = await AddRideAsync(db, fixture, status, 900, 100, 50);
        AssertSuccess(await ExecuteAsync(rider, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,RideId: trip.Id, Reason: "لم أتمكن من الدفع عبر محفظة")), "unpaid passenger cancellation before arrival");
        AssertEqual(RideStatus.Cancelled, trip.Status, "unpaid cancellation before arrival is immediate");
        Assert(await db.Notifications.AnyAsync(x => x.UserId == fixture.DriverId && x.DataJson!.Contains($"\"rideId\":{trip.Id}") && x.Title == "تم إلغاء الرحلة"), "driver notified immediately");
        Assert(!await db.PaymentTransactions.AnyAsync(x => x.RideId == trip.Id), "unpaid direct cancellation has no financial movement");
    }
    foreach (var status in new[] { RideStatus.DriverAssigned, RideStatus.DriverEnRoute, RideStatus.DriverArrived })
    {
        var trip = await AddRideAsync(db, fixture, status, 900, 100, 50);
        AssertSuccess(await ExecuteAsync(driver, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,RideId: trip.Id, Reason: "عطل في المركبة")), "driver cancellation before payment/start");
        AssertEqual(RideStatus.Cancelled, trip.Status, "driver cancels unpaid prestart directly");
    }
    var cash = new DriverCashPayment(db, config, new TestCurrentUser(fixture.DriverId), accounting);
    var paid = await AddRideAsync(db, fixture, RideStatus.InProgress, 900, 100, 50);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(paid.Id, 1000, "YER", null, "expanded-cash-consent")), "cash paid before start");
    // Represent an existing cash-paid ride before departure; collection UI retains its current timing.
    paid.Status = RideStatus.DriverArrived; paid.StartedAtUtc = null; await db.SaveChangesAsync();
    var walletBefore = await WalletBalanceAsync(db, fixture.CustomerId);
    AssertSuccess(await ExecuteAsync(rider, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,RideId: paid.Id, Reason: "لم أعد بحاجة للرحلة")), "paid passenger cancellation");
    var request = await db.RideCancellationRequests.SingleAsync(x => x.RideId == paid.Id);
    Assert(request.RequestedRefundMethod is null, "refund method requires an explicit choice");
    AssertSuccess(await ExecuteAsync(driver, "accept", new RideCancellationRequestModel(Id: request.Id)), "driver approves paid cancellation");
    AssertSuccess(await ExecuteAsync(admin, "adminApprove", new RideCancellationRequestModel(Note: "اختبار مراجعة المسؤولية والأدلة", Id: request.Id)), "administration approves paid cancellation");
    AssertEqual(RideStatus.Cancelled, paid.Status, "administrative approval is not final before refund choice");
    AssertSuccess(await ExecuteAsync(driver, "update", new RideCancellationRequestModel(Id: request.Id, RequestedRefundMethod: CashCancellationRefundMethod.CreditCustomerWallet, RefundChoiceRevision: 0)), "driver chooses wallet refund");
    AssertEqual(walletBefore, await WalletBalanceAsync(db, fixture.CustomerId), "choice cannot credit wallet early");
    AssertSuccess(await ExecuteAsync(rider, "reject", new RideCancellationRequestModel(Id: request.Id, RefundChoiceRevision: 1)), "passenger rejects wallet refund");
    AssertEqual(RideCancellationStatus.FinancialReviewPending, request.Status, "first refund refusal reaches owned financial review");
    AssertEqual(walletBefore, await WalletBalanceAsync(db, fixture.CustomerId), "rejection creates no refund");
    AssertSuccess(await ExecuteAsync(admin, "adminApprove", new RideCancellationRequestModel(Id: request.Id, Note: "راجعنا الرفض؛ يتاح رد المبلغ نقداً مع إثبات استلام الراكب")), "administration resolves the first refund-method refusal");
    AssertEqual(RideCancellationStatus.RefundMethodPending, request.Status, "review permits a concrete alternative refund method");
    Assert(!(await ExecuteAsync(driver, "update", new RideCancellationRequestModel(Id: request.Id, RequestedRefundMethod: CashCancellationRefundMethod.ReturnFromDriver, RefundChoiceRevision: 0, CashReturned: true))).Success, "stale choice cannot settle");
    db.ChangeTracker.Clear();
    AssertSuccess(await ExecuteAsync(driver, "update", new RideCancellationRequestModel(Id: request.Id, RequestedRefundMethod: CashCancellationRefundMethod.ReturnFromDriver, RefundChoiceRevision: 1, CashReturned: true)), "driver reports actual cash return");
    AssertEqual(0, await db.PaymentTransactions.CountAsync(x => x.RideId == paid.Id && x.Status == PaymentStatus.Refunded), "driver statement alone does not post a refund");
    AssertSuccess(await ExecuteAsync(rider, "accept", new RideCancellationRequestModel(Id: request.Id, RefundChoiceRevision: 2, CashReceiptConfirmed: true)), "passenger confirms cash receipt");
    AssertSuccess(await ExecuteAsync(driver, "update", new RideCancellationRequestModel(Id: request.Id, RequestedRefundMethod: CashCancellationRefundMethod.ReturnFromDriver, RefundChoiceRevision: 1, CashReturned: true)), "cash-return replay has no effect");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == paid.Id && x.Status == PaymentStatus.Refunded), "refund cycle settles once");
    AssertEqual(walletBefore, await WalletBalanceAsync(db, fixture.CustomerId), "cash-return cycle leaves passenger wallet unchanged");
    var moving = await AddRideAsync(db, fixture, RideStatus.InProgress, 900, 100, 50);
    AssertSuccess(await ExecuteAsync(driver, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,RideId: moving.Id, Reason: "حدث عطل أثناء الرحلة")), "driver requests cancellation during trip");
    var movingRequest = await db.RideCancellationRequests.SingleAsync(x => x.RideId == moving.Id);
    AssertEqual(RideCancellationStatus.CustomerReviewPending, movingRequest.Status, "started trip requires passenger decision");
    AssertSuccess(await ExecuteAsync(rider, "reject", new RideCancellationRequestModel(Id: movingRequest.Id)), "passenger rejects driver cancellation");
    AssertEqual(RideCancellationStatus.AdminReviewPending, movingRequest.Status, "passenger refusal escalates directly to administration");
    AssertSuccess(await ExecuteAsync(admin, "adminApprove", new RideCancellationRequestModel(Note: "اختبار مراجعة المسؤولية والأدلة", ReviewedTravelledFare: 0, Id: movingRequest.Id)), "admin reviews delivered fare before unpaid cancellation settlement");

    var arrivalTrip = await AddRideAsync(db, fixture, RideStatus.DriverArrived, 900, 100, 50);
    var presence = new TestRidePresence { Online = true };
    var driverArrival = new RideArrival(db, config, new TestCurrentUser(fixture.DriverId), publisher, presence);
    var riderArrival = new RideArrival(db, config, new TestCurrentUser(fixture.CustomerId), publisher, presence);
    AssertSuccess(await driverArrival.ExecuteAsync("ring", JsonSerializer.SerializeToElement(new RideArrivalModel(arrivalTrip.Id)), CancellationToken.None), "driver rings after arrival");
    var alertId = arrivalTrip.ArrivalAlertId;
    Assert(alertId != null && RideArrival.View(arrivalTrip, true).AlertActive, "arrival alert remains active until acknowledged");
    Assert(!(await driverArrival.ExecuteAsync("stop", JsonSerializer.SerializeToElement(new RideArrivalModel(arrivalTrip.Id, alertId)), CancellationToken.None)).Success, "only passenger stops alert");
    db.ChangeTracker.Clear();
    AssertSuccess(await riderArrival.ExecuteAsync("stop", JsonSerializer.SerializeToElement(new RideArrivalModel(arrivalTrip.Id, alertId)), CancellationToken.None), "passenger stops alert and acknowledges");
    AssertSuccess(await riderArrival.ExecuteAsync("stop", JsonSerializer.SerializeToElement(new RideArrivalModel(arrivalTrip.Id, alertId)), CancellationToken.None), "repeated acknowledgment is idempotent");
    AssertEqual(1, await db.Notifications.CountAsync(x => x.UserId == fixture.DriverId && x.Title == "أكد الراكب استلام تنبيه الوصول" && x.DataJson!.Contains($"\"rideId\":{arrivalTrip.Id}")), "one acknowledgment reaches driver");
    presence.Online = false;
    AssertSuccess(await driverArrival.ExecuteAsync("ring", JsonSerializer.SerializeToElement(new RideArrivalModel(arrivalTrip.Id)), CancellationToken.None), "background passenger gets notification");
    arrivalTrip = await db.Rides.SingleAsync(x => x.Id == arrivalTrip.Id);
    arrivalTrip.PassengerBoardedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    Assert(!RideArrival.View(arrivalTrip, true).AlertActive && !RideArrival.View(arrivalTrip, true).CanRing, "boarding suppresses existing and future arrival alerts");
    Assert(!(await driverArrival.ExecuteAsync("ring", JsonSerializer.SerializeToElement(new RideArrivalModel(arrivalTrip.Id)), CancellationToken.None)).Success, "driver cannot ring after boarding before trip start");
    Assert(await db.Notifications.AnyAsync(x => x.UserId == fixture.DriverId && x.Title == "الراكب غير متصل بالتطبيق حالياً"), "driver informed passenger is offline");

    var legacyRide = await AddRideAsync(db, fixture, RideStatus.Completed, 900, 100, 50);
    var driverAccount = await db.LedgerAccounts.SingleAsync(x => x.Code == $"1100-DRV-{fixture.DriverId}");
    var expenseAccount = await db.LedgerAccounts.SingleAsync(x => x.Purpose == LedgerAccountPurpose.DriverCommission && x.IsPosting);
    var revenue = await db.LedgerAccounts.SingleAsync(x => x.Code == $"4200-SK-{fixture.ServiceKindId}");
    var posting = new AccountingPostingService(db);
    var legacy = await posting.PostAsync(new AccountingPostingRequest(JournalEntryType.RideCashCollection, "LEGACY-CASH", "Original legacy cash journal", "YER",
        [new(driverAccount.Id, Debit: 1000, FinancialPartyId: driverAccount.FinancialPartyId, UserId: fixture.DriverId, RideId: legacyRide.Id),
         new(revenue.Id, Credit: 1000, RideId: legacyRide.Id), new(expenseAccount.Id, Debit: 50, UserId: fixture.DriverId, RideId: legacyRide.Id),
         new(driverAccount.Id, Credit: 50, FinancialPartyId: driverAccount.FinancialPartyId, UserId: fixture.DriverId, RideId: legacyRide.Id)],
        SourceType: "RideCashPayment", SourceId: "legacy-example"), CancellationToken.None);
    var correction = new CashCollectionAccountingReconciliation(db, posting);
    AssertEqual(1, await correction.RunAsync(CancellationToken.None), "old cash share corrected once");
    AssertEqual(0, await correction.RunAsync(CancellationToken.None), "cash share correction replay creates no journal");
    AssertEqual(1050m, (await db.JournalEntries.SingleAsync(x => x.Id == legacy.JournalEntryId)).TotalDebit, "original cash journal preserved");
    var adjustment = await db.JournalEntries.Include(x => x.Lines).SingleAsync(x => x.SourceType == "CashCollectionShareCorrection" && x.SourceId == legacy.JournalEntryId.ToString());
    AssertEqual(800m, adjustment.TotalDebit, "independent correction recognizes missing actual driver share");
    AssertEqual(800m, adjustment.TotalCredit, "correction is balanced");
    Console.WriteLine("PASS: immediate pre-arrival cancellation, first refund refusal review, verified cash receipt, stale/repeated decisions, driver escalation, arrival acknowledgment/offline notification and legacy accounting correction.");
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
    db.DriverProfiles.Add(new DriverProfile { ApprovedPassengerSeats = 6,
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
    var pricingQuote = new RidePricingQuote
    {
        CustomerId = fixture.CustomerId, ServiceKindId = fixture.ServiceKindId, ServiceCatalogItemId = fixture.ServiceCatalogItemId,
        PickupLatitude = latitude, PickupLongitude = longitude, DestinationLatitude = latitude + .02, DestinationLongitude = longitude + .02,
        Amount = 1000, MinPrice = 800, MaxPrice = 2000, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };
    db.RidePricingQuotes.Add(pricingQuote);
    await db.SaveChangesAsync();
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
        IdempotencyKey: "realtime-event-ride",
        PricingQuoteToken: pricingQuote.Token,
        MoreThanFourPassengers: true, PassengerNotes: "ملاحظة اختبار للركوب"));
    AssertSuccess(rideResult, "ride request before rideAvailable event");
    var rideId = Convert.ToInt32(rideResult.Data!.GetType().GetProperty("Id")!.GetValue(rideResult.Data));
    Assert(publisher.AvailableRecipients.Count == 1, "rideAvailable targets the matched driver only");
    var available = publisher.AvailableRecipients.Single();
    AssertEqual(candidateDriver.Id, available.UserId, "rideAvailable recipient is the eligible driver");
    AssertEqual(rideId, Convert.ToInt32(available.Ride["id"]), "rideAvailable contains the saved ride id");
    Assert(Equals(true, available.Ride["destinationAvailable"]), "rideAvailable provides destination preview to its eligible driver before an offer");
    Assert(Equals(latitude + 0.02, available.Ride["destinationLatitude"]), "rideAvailable includes the requested destination coordinates");
    Assert(available.Ride["customerName"] is null && available.Ride["customerPhone"] is null,
        "preoffer route preview does not reveal customer identity or phone");
    Assert(Equals(true, available.Ride["moreThanFourPassengers"]), "riding options reach eligible drivers before offers");
    Assert(Equals("ملاحظة اختبار للركوب", available.Ride["passengerNotes"]), "passenger notes reach drivers");
    var savedOptions = await db.Rides.AsNoTracking().SingleAsync(x => x.Id == rideId);
    Assert(savedOptions.MoreThanFourPassengers && savedOptions.PassengerNotes == "ملاحظة اختبار للركوب", "riding options persist in the database");

    var driverViews = new YemenDrive.Application.Rides.RideSearchView(
        db, configuration, new TestCurrentUser(candidateDriver.Id), publisher);
    var customerViews = new YemenDrive.Application.Rides.RideSearchView(
        db, configuration, new TestCurrentUser(fixture.CustomerId), publisher);
    Assert(!(await ExecuteAsync(customerViews, "update", new RideSearchViewModel(rideId, 1_000m))).Success,
        "a customer cannot fabricate a driver view");
    AssertSuccess(await ExecuteAsync(driverViews, "update", new RideSearchViewModel(rideId, 1_000m)), "driver records an actual view");
    AssertSuccess(await ExecuteAsync(driverViews, "update", new RideSearchViewModel(rideId, 1_000m)), "duplicate view is idempotent");
    AssertEqual(1, await db.RideSearchViews.CountAsync(x => x.RideId == rideId), "one view per driver");
    AssertSuccess(await ExecuteAsync(customerViews, "get", new RideSearchViewModel(rideId)), "owner reads real viewers");
    Assert(!(await ExecuteAsync(driverViews, "get", new RideSearchViewModel(rideId))).Success,
        "a driver cannot read the customer's viewers");

    var priceSearch = new RideSearchAction(db, configuration, new TestCurrentUser(fixture.CustomerId), publisher);
    var preliminaryOffer = new RideOfferService(db, configuration, new TestCurrentUser(candidateDriver.Id), publisher);
    AssertSuccess(await ExecuteAsync(preliminaryOffer, "add", new RideOfferModel(RideId: rideId, Amount: 950m)),
        "pending offer exists before price amendment");
    AssertSuccess(await ExecuteAsync(priceSearch, "update", new RideSearchActionModel(rideId, CustomerPrice: 1_200m)),
        "price change restarts the same ride atomically");
    AssertEqual(1_200m, (await db.Rides.AsNoTracking().SingleAsync(x => x.Id == rideId)).CustomerPrice!.Value,
        "new price is persisted");
    AssertEqual(0, await db.RideSearchViews.CountAsync(x => x.RideId == rideId), "new search clears old views");
    Assert(!await db.RideOffers.AnyAsync(x => x.RideId == rideId && x.Status == OfferStatus.Pending),
        "offers for the previous search are expired");
    var noticeCount = await db.Notifications.CountAsync();
    AssertSuccess(await ExecuteAsync(priceSearch, "update", new RideSearchActionModel(rideId, CustomerPrice: 1_200m)),
        "retry of saved price is idempotent");
    AssertEqual(noticeCount, await db.Notifications.CountAsync(), "same price retry does not notify twice");
    Assert(!(await ExecuteAsync(driverViews, "update", new RideSearchViewModel(rideId, 1_000m))).Success,
        "stale price view is rejected");

    var offerService = new RideOfferService(
        db, configuration, new TestCurrentUser(candidateDriver.Id), publisher);
    var offerResult = await ExecuteAsync(offerService, "add", new RideOfferModel(
        RideId: rideId, Amount: 950m, ValidForSeconds: 90));
    AssertSuccess(offerResult, "driver offer before offerReceived event");
    Assert(publisher.OfferReceivedCustomerId == fixture.CustomerId, "offerReceived targets the ride customer");
    AssertEqual(rideId, publisher.ReceivedOffer!.RideId, "offerReceived contains the saved offer's ride id");
    AssertEqual(OfferStatus.Pending, publisher.ReceivedOffer.Status, "offerReceived carries the persisted pending status");
    Assert(Math.Abs((publisher.ReceivedOffer.ExpiresAtUtc - publisher.ReceivedOffer.CreatedAtUtc).TotalSeconds - 60) < 1,
        "server enforces a one-minute offer lifetime regardless of the requested duration");
    var offerToExpire = await db.RideOffers.SingleAsync(x => x.Id == publisher.ReceivedOffer.Id);
    offerToExpire.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
    await db.SaveChangesAsync();
    var expiration = new RideOfferExpiration(db, publisher);
    AssertEqual(1, await expiration.ExpireAsync(CancellationToken.None), "server expires an overdue offer without an open passenger screen");
    AssertEqual(0, await expiration.ExpireAsync(CancellationToken.None), "expiration is idempotent");
    Assert(publisher.UpdatedOffers.Any(x => x.Id == offerToExpire.Id && x.Status == OfferStatus.Expired), "expiration is broadcast");
    var expiredAction = new RideOfferAction(db, configuration, new TestCurrentUser(fixture.CustomerId), publisher);
    Assert(!(await ExecuteAsync(expiredAction, "accept", new RideOfferActionModel(RideId: rideId, OfferId: offerToExpire.Id))).Success,
        "an expired offer cannot be accepted");
    AssertSuccess(await ExecuteAsync(offerService, "add", new RideOfferModel(RideId: rideId, Amount: 950m)), "driver sends another offer");

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
    Assert(!(await ExecuteAsync(priceSearch, "update", new RideSearchActionModel(rideId, CustomerPrice: 1_500m))).Success,
        "assigned ride cannot have its price changed or search restarted");

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
        IdempotencyKey: "realtime-event-cancel", PricingQuoteToken: pricingQuote.Token));
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
    AssertSuccess(await ExecuteAsync(searchAction, "cancel", new RideSearchActionModel(RideId: secondRideId, Reason: "ارتفاع الأجرة")),
        "cancellation reason can be recorded after the search deadline");
    AssertSuccess(await ExecuteAsync(searchAction, "cancel", new RideSearchActionModel(RideId: secondRideId, Reason: "ارتفاع الأجرة")),
        "retrying cancellation does not create a duplicate reason");
    AssertEqual(1, await db.RideCancellationRequests.CountAsync(x => x.RideId == secondRideId && x.Reason == "ارتفاع الأجرة"), "one persisted cancellation reason");
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
    builder.Services.AddSingleton(RealtimeScenarioMode.Settings ?? new YemenDrive.Database.Realtime.RealtimeJournalSettings());
    builder.Services.AddDbContext<YemenDriveDbContext>(options => options.UseSqlServer(connectionString));
    builder.Services.AddScoped<AuthSessionService>();
    builder.Services.AddScoped<TrackingHubAccessPolicy>();
    builder.Services.AddScoped<DriverLocationIngestionService>();
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
        await using var notificationDb = new YemenDriveDbContext(notificationOptions, RealtimeScenarioMode.Settings);
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
        var productionStore = new OtpChallengeStore(consoleOtpEnabled: false);
        Assert(productionStore.TryCreate("700000001", "signIn") is null, "OTP cannot be created outside Development while no SMS provider is configured");
        Assert(capturedOutput.ToString().Length == 0, "production OTP requests must not print to the console");

        var developmentStore = new OtpChallengeStore(consoleOtpEnabled: true);
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

    db.DriverProfiles.Add(new DriverProfile { ApprovedPassengerSeats = 4,
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
        x.SourceType == "RideWalletPayment" && x.Lines.Any(l => l.RideId == ride.Id));
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
    Assert(!(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,RideId: completedRide.Id, Reason: "لا أريد المتابعة"))).Success,
        "completed ride cannot be cancelled or refunded");

    // An unpaid cancellation after arrival requires the driver's review.
    var ride = await AddRideAsync(db, fixture, RideStatus.DriverArrived, 900m, 100m, 50m);
    Assert(!(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,RideId: ride.Id, Reason: " "))).Success,
        "cancellation reason is mandatory");
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,RideId: ride.Id, Reason: "تغيرت خططي اليوم")), "customer cancellation request");
    db.ChangeTracker.Clear();
    var first = await db.RideCancellationRequests.SingleAsync(x => x.RideId == ride.Id);
    AssertEqual(RideCancellationStatus.DriverReviewPending, first.Status, "pre-start cancellation is sent to driver");
    AssertEqual(RideStatus.Cancelled, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "ride stops while cancellation is reviewed");
    var driverCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    AssertSuccess(await ExecuteAsync(driverCancellation, "reject", new RideCancellationRequestModel(Id: first.Id, Note: "لا أوافق")), "driver rejects cancellation");
    db.ChangeTracker.Clear();
    AssertEqual(RideCancellationStatus.AdminReviewPending, await db.RideCancellationRequests.Where(x => x.Id == first.Id).Select(x => x.Status).SingleAsync(), "first rejection escalates directly");
    Assert(!(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: "retry"))).Success, "closed transport cannot start another cancellation");
    var adminCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.AdminId), RideAccounting(db));
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: first.Id, Note: "تمت مراجعة المسؤولية دون نقل منفذ")), "admin approves cancellation");
    AssertEqual(RideStatus.Cancelled, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "admin decision keeps transport closed");

}

static async Task CashSettlementHandlesExcessAndShortageAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var ride = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    var before = await WalletBalanceAsync(db, fixture.CustomerId);
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 1100m, "YER", IdempotencyKey: "cash-excess")), "physical cash receipt");
    AssertEqual(before, await WalletBalanceAsync(db, fixture.CustomerId), "no automatic cash-change wallet credit");
    var change = await db.RideFinancialCases.SingleAsync(x => x.RideId == ride.Id && x.Kind == "CashChange");
    AssertEqual(100m, change.Amount, "exact cash change entitlement");
    var choice = new RideFinancialCaseService(db, configuration, new TestCurrentUser(fixture.CustomerId));
    AssertSuccess(await choice.ExecuteAsync("accept", JsonSerializer.SerializeToElement(new RideFinancialCaseModel(Id: change.Id)), default), "rider chooses wallet for change");
    AssertEqual(before + 100m, await WalletBalanceAsync(db, fixture.CustomerId), "consented credit only once");
    var shortage = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(shortage.Id, 900m, "YER", IdempotencyKey: "cash-shortage")), "record actual partial receipt");
    AssertEqual(before + 100m, await WalletBalanceAsync(db, fixture.CustomerId), "partial cash does not consume wallet automatically");
    AssertEqual(100m, (await RideFinancialState.ReadAsync(db, shortage, default)).OutstandingAmount, "partial cash leaves real outstanding balance");
    AssertEqual(0, await db.CashCollectionApprovals.CountAsync(x => x.RideId == shortage.Id), "new cash flow needs no automatic wallet-shortage approval");
    // This fixture's legacy partial receipt is explicitly completed for subsequent scenarios.
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(shortage.Id, 100m, "YER", IdempotencyKey: "cash-shortage-remainder")), "second actual cash receipt");
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
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,
        RideId: walletRefundRide.Id, Reason: "تغيرت خطط العميل", RequestedRefundMethod: CashCancellationRefundMethod.CreditCustomerWallet)), "customer asks cancellation with wallet refund");
    var walletRequest = await db.RideCancellationRequests.SingleAsync(x => x.RideId == walletRefundRide.Id);
    AssertSuccess(await ExecuteAsync(driverCancellation, "accept", new RideCancellationRequestModel(Id: walletRequest.Id)), "driver accepts paid cancellation");
    db.ChangeTracker.Clear();
    AssertEqual(RideCancellationStatus.AdminReviewPending, await db.RideCancellationRequests.Where(x => x.Id == walletRequest.Id).Select(x => x.Status).SingleAsync(), "paid cancellation awaits administration");
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: walletRequest.Id, Note: "سجل قديم دون تسعيرة مسار؛ لم تُقطع مسافة", ReviewedTravelledFare: 0m)), "admin settles cash cancellation to wallet");
    db.ChangeTracker.Clear();
    AssertEqual(RideCancellationStatus.RefundMethodPending, await db.RideCancellationRequests.Where(x => x.Id == walletRequest.Id).Select(x => x.Status).SingleAsync(), "admin approval awaits driver refund choice");
    AssertSuccess(await ExecuteAsync(driverCancellation, "update", new RideCancellationRequestModel(Id: walletRequest.Id,
        RequestedRefundMethod: CashCancellationRefundMethod.CreditCustomerWallet, RefundChoiceRevision: 0)), "driver chooses wallet refund");
    AssertEqual(balanceBeforeWalletRefund, await WalletBalanceAsync(db, fixture.CustomerId), "no refund before passenger consent");
    AssertSuccess(await ExecuteAsync(customerCancellation, "accept", new RideCancellationRequestModel(Id: walletRequest.Id, RefundChoiceRevision: 1)), "passenger accepts wallet refund");
    db.ChangeTracker.Clear();
    AssertEqual(balanceBeforeWalletRefund + 850m, await WalletBalanceAsync(db, fixture.CustomerId), "administrative decision credits refund less service and cancellation fees");
    AssertEqual(RideStatus.Cancelled, await db.Rides.Where(x => x.Id == walletRefundRide.Id).Select(x => x.Status).SingleAsync(), "administrative decision cancels paid ride");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == walletRefundRide.Id && x.Provider == "CancellationRefundToWallet" && x.Status == PaymentStatus.Refunded), "administrative wallet refund creates immutable payment record");
    AssertEqual(-850m, await db.DriverSettlements.Where(x => x.PaymentReference != null && x.PaymentReference.StartsWith("cancellation:") && x.DriverId == fixture.DriverId).OrderByDescending(x => x.Id).Select(x => x.NetPayable).FirstAsync(), "administrative wallet refund creates driver receivable");
    var originalWalletPaymentId = await db.PaymentTransactions.Where(x => x.RideId == walletRefundRide.Id && x.Provider == "Cash" && x.Status == PaymentStatus.Paid).Select(x => x.Id).SingleAsync();
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.Lines.Any(l => l.RideId == walletRefundRide.Id)), "administrative wallet refund posts one reversal journal");
    Assert(!(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Note: "اختبار مراجعة المسؤولية والأدلة", Id: walletRequest.Id))).Success, "closed administrative approval cannot be repeated");
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.Lines.Any(l => l.RideId == walletRefundRide.Id)), "repeated administrative approval does not duplicate journal");

    var directReturnRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 50m);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(directReturnRide.Id, 1_000m, "YER", null, "admin-cancel-direct-cash")), "cash payment before direct return");
    var balanceBeforeDirectReturn = await WalletBalanceAsync(db, fixture.CustomerId);
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,
        RideId: directReturnRide.Id, Reason: "لم تعد الرحلة مطلوبة", RequestedRefundMethod: CashCancellationRefundMethod.ReturnFromDriver)), "customer asks direct cash return");
    var directRequest = await db.RideCancellationRequests.SingleAsync(x => x.RideId == directReturnRide.Id);
    AssertSuccess(await ExecuteAsync(driverCancellation, "accept", new RideCancellationRequestModel(Id: directRequest.Id)), "driver accepts direct-return cancellation");
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Note: "اختبار مراجعة المسؤولية والأدلة", Id: directRequest.Id, ReviewedTravelledFare: 0m)), "admin settles direct cash return");
    db.ChangeTracker.Clear();
    AssertSuccess(await ExecuteAsync(driverCancellation, "update", new RideCancellationRequestModel(Id: directRequest.Id,
        RequestedRefundMethod: CashCancellationRefundMethod.ReturnFromDriver, RefundChoiceRevision: 0, CashReturned: true)), "driver confirms cash refund after approval");
    AssertSuccess(await ExecuteAsync(customerCancellation, "accept", new RideCancellationRequestModel(Id: directRequest.Id, RefundChoiceRevision: 1)), "customer independently confirms receiving cash refund");
    db.ChangeTracker.Clear();
    AssertEqual(balanceBeforeDirectReturn, await WalletBalanceAsync(db, fixture.CustomerId), "direct cash return does not change customer wallet");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == directReturnRide.Id && x.Provider == "CashReturnFromDriver" && x.Status == PaymentStatus.Refunded), "direct return creates immutable payment record");
    var originalDirectPaymentId = await db.PaymentTransactions.Where(x => x.RideId == directReturnRide.Id && x.Provider == "Cash" && x.Status == PaymentStatus.Paid).Select(x => x.Id).SingleAsync();
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.Lines.Any(l => l.RideId == directReturnRide.Id)), "direct return posts one reversal journal");

    // Wallet payment is currently accepted at trip completion. Arrange a
    // historical paid ride that is subsequently stopped while in progress so
    // the cancellation settlement branch remains covered independently from
    // the payment-timing workflow.
    var walletPaymentRide = await AddRideAsync(db, fixture, RideStatus.Completed, 900m, 100m, 50m);
    var fundedCustomerWallet = await db.Wallets.SingleAsync(x => x.UserId == fixture.CustomerId);
    fundedCustomerWallet.Balance += 1000m;
    await db.SaveChangesAsync();
    var walletPayment = new Payment(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db));
    var customerBeforeWalletCancellation = await WalletBalanceAsync(db, fixture.CustomerId);
    var driverBeforeWalletCancellation = await WalletBalanceAsync(db, fixture.DriverId);
    AssertSuccess(await ExecuteAsync(walletPayment, "add", new PaymentModel(
        RideId: walletPaymentRide.Id, Amount: 1_000m, Currency: "YER", Provider: "YemenDriveWallet", Status: PaymentStatus.Paid,
        IdempotencyKey: "admin-cancel-yemen-drive-wallet")), "wallet payment before administrative cancellation");
    walletPaymentRide.Status = RideStatus.InProgress;
    walletPaymentRide.CompletedAtUtc = null;
    await db.SaveChangesAsync();
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(SafeStopConfirmed: true,
        RideId: walletPaymentRide.Id, Reason: "تغيرت خطط العميل بعد الدفع")), "customer asks cancellation after wallet payment");
    var walletPaymentRequest = await db.RideCancellationRequests.SingleAsync(x => x.RideId == walletPaymentRide.Id);
    AssertSuccess(await ExecuteAsync(driverCancellation, "accept", new RideCancellationRequestModel(Id: walletPaymentRequest.Id)), "driver accepts wallet-paid cancellation");
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Note: "اختبار مراجعة المسؤولية والأدلة", Id: walletPaymentRequest.Id, ReviewedTravelledFare: 0m)), "admin settles Yemen Drive wallet cancellation");
    db.ChangeTracker.Clear();
    AssertEqual(customerBeforeWalletCancellation - 150m, await WalletBalanceAsync(db, fixture.CustomerId), "wallet cancellation retains service and cancellation fees");
    AssertEqual(driverBeforeWalletCancellation, await WalletBalanceAsync(db, fixture.DriverId), "wallet payment cancellation reverses the driver's credited share");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == walletPaymentRide.Id && x.Provider == "CancellationRefundToWallet" && x.Status == PaymentStatus.Refunded), "wallet payment cancellation creates refund record");
    var originalYemenDriveWalletPaymentId = await db.PaymentTransactions.Where(x => x.RideId == walletPaymentRide.Id && x.Provider == "YemenDriveWallet" && x.Status == PaymentStatus.Paid).Select(x => x.Id).SingleAsync();
    AssertEqual(1, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCancellation" && x.Lines.Any(l => l.RideId == walletPaymentRide.Id)), "wallet payment cancellation posts one reversal journal");
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
    var wallet = await db.Wallets.SingleAsync(x => x.UserId == fixture.CustomerId); wallet.Balance = 50m; await db.SaveChangesAsync();
    var ride = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 800m, "YER", IdempotencyKey: "cash-insufficient")), "partial cash remains valid despite insufficient app wallet");
    AssertEqual(50m, await WalletBalanceAsync(db, fixture.CustomerId), "insufficient app wallet is untouched");
    AssertEqual(200m, (await RideFinancialState.ReadAsync(db, ride, default)).OutstandingAmount, "only missing difference remains unpaid");
    AssertEqual(800m, await db.PaymentTransactions.Where(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid).SumAsync(x => x.Amount), "real cash receipt is never discarded");
}

static async Task ExternalWalletPaymentPostsOnceAndLocksOtherTendersAsync(
    YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var method = new YemenDrive.Database.Entities.PaymentMethod
    {
        Code = "floosak-test", NameAr = "فلوسك اختبار", DescriptionAr = "اختبار", ProviderCode = "Floosak",
        Kind = PaymentMethodKind.ExternalWallet, IsActive = true, IsAvailableForRidePayment = true
    };
    db.PaymentMethods.Add(method);
    await db.SaveChangesAsync();
    await new FinancialAccountProvisioningService(db).ProvisionPaymentMethodAsync(method, CancellationToken.None);
    await db.SaveChangesAsync();
    var ride = await AddRideAsync(db, fixture, RideStatus.Completed, 900m, 100m, 50m);
    var operation = new PaymentWalletOperation
    {
        RideId = ride.Id, CustomerId = fixture.CustomerId, PaymentMethodId = method.Id,
        RequestId = Guid.NewGuid().ToString("N"), Status = "AwaitingCode", IsOpen = true,
        Amount = 1_000m, Mobile = "967777000001", CustomerName = "عميل اختبار", Fees = 10m,
        FeesKnown = true, ProviderReference = "simulated-provider-reference"
    };
    db.PaymentWalletOperations.Add(operation);
    await db.SaveChangesAsync();
    var originalCustomer = await WalletBalanceAsync(db, fixture.CustomerId);
    var originalDriver = await WalletBalanceAsync(db, fixture.DriverId);
    var realtime = new RecordingRideRealtimePublisher();
    var settlement = new ExternalWalletPaymentSettlement(db, RideAccounting(db), realtime);
    await settlement.ValidateAsync(ride, method.Id, CancellationToken.None);
    var rejected = false;
    try { await settlement.SettleAsync(operation, CancellationToken.None); }
    catch (ServiceException ex) when (ex.Code == "provider_payment_not_confirmed") { rejected = true; }
    Assert(rejected, "step success must not be posted as financial success");
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    var wallet = new Payment(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db));
    Assert(!(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 1_000m))).Success,
        "cash cannot bypass a provider operation awaiting OTP");
    Assert(!(await ExecuteAsync(wallet, "add", new PaymentModel(RideId: ride.Id, Amount: 1_000m, Provider: "YemenDriveWallet"))).Success,
        "internal wallet cannot bypass a provider operation awaiting OTP");
    AssertEqual(0, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id), "no payment before provider success");
    operation.Status = "ProviderSucceeded";
    operation.ProviderSucceededAtUtc = DateTime.UtcNow;
    db.PaymentTransactions.Add(new PaymentTransaction
    {
        UserId = fixture.CustomerId, Amount = 1m, Provider = "DifferentProvider",
        ProviderReference = operation.ProviderReference, Status = PaymentStatus.Pending
    });
    await db.SaveChangesAsync();

    // A local accounting failure after provider success must roll back the local money effects,
    // retain the durable success evidence, and permit posting without another withdrawal.
    var providerAccount = await db.LedgerAccounts.SingleAsync(x => x.Code == $"1200-PM-{method.Id}");
    providerAccount.IsActive = false;
    await db.SaveChangesAsync();
    try { await settlement.SettleAsync(operation, CancellationToken.None); throw new InvalidOperationException("disabled ledger accepted"); }
    catch (ServiceException ex) when (ex.Code == "provider_ledger_not_found") { }
    db.ChangeTracker.Clear();
    AssertEqual(originalDriver, await WalletBalanceAsync(db, fixture.DriverId), "driver balance rolls back on accounting failure");
    AssertEqual(0, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id), "payment rolls back on accounting failure");
    operation = await db.PaymentWalletOperations.SingleAsync(x => x.Id == operation.Id);
    AssertEqual("ProviderSucceeded", operation.Status, "provider evidence survives local posting failure");
    providerAccount = await db.LedgerAccounts.SingleAsync(x => x.Code == $"1200-PM-{method.Id}");
    providerAccount.IsActive = true;
    await db.SaveChangesAsync();
    var payment = await settlement.SettleAsync(operation, CancellationToken.None);
    var repeated = await settlement.SettleAsync(operation, CancellationToken.None);
    AssertEqual(payment.Id, repeated.Id, "provider success retry returns same payment");
    AssertEqual("Succeeded", operation.Status, "receipt is available only after accounting commits");
    AssertEqual(originalCustomer, await WalletBalanceAsync(db, fixture.CustomerId), "external withdrawal never debits internal customer wallet");
    AssertEqual(originalDriver + 850m, await WalletBalanceAsync(db, fixture.DriverId), "driver gets net share once");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id), "one external payment");
    AssertEqual(1, await db.WalletTransactions.CountAsync(x => x.RideId == ride.Id), "one driver credit");
    var journal = await db.JournalEntries.Include(x => x.Lines).SingleAsync(x =>
        x.SourceType == "RideExternalWalletPayment" && x.SourceId == payment.IdempotencyKey);
    AssertEqual(journal.TotalDebit, journal.TotalCredit, "external journal balances");
    AssertEqual(1_000m, journal.Lines.Single(x => x.LedgerAccountId == providerAccount.Id).Debit,
        "provider account records ride principal, provider fee is not platform revenue");
    var driverWalletAccountId = await db.LedgerAccounts.Where(x => x.Code == $"2100-WLT-{fixture.DriverWalletId}")
        .Select(x => x.Id).SingleAsync();
    AssertEqual(850m, journal.Lines.Single(x => x.LedgerAccountId == driverWalletAccountId).Credit,
        "driver wallet liability reconciles to the actual wallet credit");
    AssertEqual("Floosak", payment.Provider, "catalog code cannot impersonate a built-in payment provider");
    AssertEqual(1, realtime.Payments.Count, "successful payment broadcasts once");
    Assert(!(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 1_000m))).Success,
        "cash cannot collect a confirmed wallet payment");
    Assert(!(await ExecuteAsync(wallet, "update", new PaymentModel(Id: payment.Id, Status: PaymentStatus.Failed))).Success,
        "client cannot alter provider-confirmed payment");
    var forged = new PaymentTransaction { UserId = fixture.CustomerId, RideId = null, Amount = 1m, Provider = "Floosak" };
    db.PaymentTransactions.Add(forged);
    await db.SaveChangesAsync();
    Assert(!(await ExecuteAsync(wallet, "update", new PaymentModel(Id: forged.Id, Status: PaymentStatus.Paid))).Success,
        "client cannot turn a pending provider record into paid");
    var zeroNetRide = await AddRideAsync(db, fixture, RideStatus.Completed, 50m, 100m, 50m);
    var zeroNetOperation = new PaymentWalletOperation
    {
        RideId = zeroNetRide.Id, CustomerId = fixture.CustomerId, PaymentMethodId = method.Id,
        RequestId = Guid.NewGuid().ToString("N"), Status = "ProviderSucceeded", IsOpen = true,
        Amount = 150m, Mobile = "967777000001", ProviderReference = "simulated-zero-net-reference",
        ProviderSucceededAtUtc = DateTime.UtcNow
    };
    db.PaymentWalletOperations.Add(zeroNetOperation);
    await db.SaveChangesAsync();
    await settlement.ValidateAsync(zeroNetRide, method.Id, CancellationToken.None);
    await settlement.SettleAsync(zeroNetOperation, CancellationToken.None);
    AssertEqual("Succeeded", zeroNetOperation.Status, "zero driver entitlement does not strand an actual provider debit");
    AssertEqual(0, await db.WalletTransactions.CountAsync(x => x.RideId == zeroNetRide.Id), "zero wallet movements are omitted");
    AssertEqual(originalDriver + 850m, await WalletBalanceAsync(db, fixture.DriverId), "zero entitlement leaves driver balance unchanged");

    var jaibMethod = new YemenDrive.Database.Entities.PaymentMethod
    {
        Code = "jaib-test", NameAr = "جيب اختبار", DescriptionAr = "اختبار", ProviderCode = "Jaib",
        Kind = PaymentMethodKind.ExternalWallet, IsActive = true, IsAvailableForRidePayment = true
    };
    db.PaymentMethods.Add(jaibMethod);
    await db.SaveChangesAsync();
    await new FinancialAccountProvisioningService(db).ProvisionPaymentMethodAsync(jaibMethod, CancellationToken.None);
    await db.SaveChangesAsync();
    var jaibRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 50m);
    jaibRide.CustomerPaymentEnabled = true;
    var jaibOperation = new PaymentWalletOperation
    {
        RideId = jaibRide.Id, CustomerId = fixture.CustomerId, PaymentMethodId = jaibMethod.Id,
        ProviderCode = "Jaib", RequestId = Guid.NewGuid().ToString("N"), Status = "ProviderSucceeded",
        Amount = 1000m, Mobile = "777000001", ProviderReference = "simulated-provider-reference",
        ProviderSucceededAtUtc = DateTime.UtcNow
    };
    db.PaymentWalletOperations.Add(jaibOperation);
    await db.SaveChangesAsync();
    await settlement.ValidateAsync(jaibRide, jaibMethod.Id, CancellationToken.None);
    var jaibPayment = await settlement.SettleAsync(jaibOperation, CancellationToken.None);
    var jaibRetry = await settlement.SettleAsync(jaibOperation, CancellationToken.None);
    AssertEqual(jaibPayment.Id, jaibRetry.Id, "Jaib posting remains idempotent");
    AssertEqual("Jaib", jaibPayment.Provider, "Jaib keeps canonical provider identity");
    Assert(jaibPayment.ProviderReference!.StartsWith($"Jaib:{jaibMethod.Id}:"), "Jaib references cannot collide with Floosak references");
    AssertEqual(originalCustomer, await WalletBalanceAsync(db, fixture.CustomerId), "Jaib never debits the internal customer wallet");
    AssertEqual(originalDriver + 1700m, await WalletBalanceAsync(db, fixture.DriverId), "Jaib credits driver share once");
    var jaibAccountId = await db.LedgerAccounts.Where(x => x.Code == $"1200-PM-{jaibMethod.Id}").Select(x => x.Id).SingleAsync();
    var jaibJournal = await db.JournalEntries.Include(x => x.Lines).SingleAsync(x =>
        x.SourceType == "RideExternalWalletPayment" && x.SourceId == jaibPayment.IdempotencyKey);
    AssertEqual(1000m, jaibJournal.Lines.Single(x => x.LedgerAccountId == jaibAccountId).Debit, "Jaib posts to its own provider account");
    AssertEqual(jaibJournal.TotalDebit, jaibJournal.TotalCredit, "Jaib journal balances");
    Assert(!(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(jaibRide.Id, 1000m))).Success,
        "driver cannot collect cash after Jaib success");
}

static async Task CustomerPaymentAccessChangesArePublishedAsync(YemenDriveDbContext db,
    DatabaseConfigurationStore configuration, Fixture fixture, DbContextOptions<YemenDriveDbContext> observerOptions)
{
    var ride = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    var publishedAfterCommit = 0;
    var realtime = new RecordingRideRealtimePublisher
    {
        ObserveRideStatusAsync = async (change, token) =>
        {
            // A separate connection must already observe the change when a rider is notified.
            // Publishing while the transaction still owns its write lock would time out here.
            await using var observer = new YemenDriveDbContext(observerOptions, RealtimeScenarioMode.Settings);
            observer.Database.SetCommandTimeout(5);
            var saved = await observer.Rides.AsNoTracking().SingleAsync(x => x.Id == change.RideId, token);
            AssertEqual(saved.CustomerPaymentEnabled, change.CustomerPaymentEnabled, "realtime payment access matches committed data");
            publishedAfterCommit++;
        }
    };
    var driver = new RideService(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db), realtime);
    AssertSuccess(await ExecuteAsync(driver, "update", new RideModel(Id: ride.Id, CustomerPaymentEnabled: true)), "driver enables rider payment");
    AssertEqual(1, realtime.RideStatuses.Count, "enabling payment publishes even without a status or driver change");
    var enabled = realtime.RideStatuses.Single();
    Assert(enabled.CustomerPaymentEnabled && enabled.CustomerId == fixture.CustomerId && enabled.DriverId == fixture.DriverId &&
        enabled.Status == RideStatus.InProgress && enabled.RideId == ride.Id, "rider receives enabled payment flag for the existing ride");

    AssertSuccess(await ExecuteAsync(driver, "update", new RideModel(Id: ride.Id, CustomerPaymentEnabled: true)), "same-value payment access retry");
    AssertEqual(1, realtime.RideStatuses.Count, "unchanged payment access does not duplicate the event");
    var customer = new RideService(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db), realtime);
    Assert(!(await ExecuteAsync(customer, "update", new RideModel(Id: ride.Id, CustomerPaymentEnabled: false))).Success,
        "rider cannot change own payment access");
    AssertEqual(1, realtime.RideStatuses.Count, "rejected access change is not broadcast");

    AssertSuccess(await ExecuteAsync(driver, "update", new RideModel(Id: ride.Id, CustomerPaymentEnabled: false)), "driver disables payment before collection");
    AssertEqual(2, realtime.RideStatuses.Count, "disabling payment also publishes immediately");
    Assert(!realtime.RideStatuses.Last().CustomerPaymentEnabled, "rider receives disabled payment flag");
    AssertEqual(2, publishedAfterCommit, "both payment access changes publish after commit");
    Console.WriteLine("PASS: rider payment enable/disable emits committed realtime state; unchanged or rejected requests do not emit.");
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

file sealed class RouteHistoryReadFailure : DbCommandInterceptor
{
    private bool _failed;
    public int ReadAttempts { get; private set; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("DATEDIFF(second", StringComparison.OrdinalIgnoreCase))
        {
            ReadAttempts++;
            if (!_failed)
            {
                _failed = true;
                throw new TimeoutException("Isolated optional display archive timeout.");
            }
        }
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
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
    public Func<RideStatusRealtimeEvent, CancellationToken, Task>? ObserveRideStatusAsync { get; init; }
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

    public async Task PublishRideStatusUpdatedAsync(
        RideStatusRealtimeEvent rideStatus,
        CancellationToken cancellationToken)
    {
        if (ObserveRideStatusAsync is not null) await ObserveRideStatusAsync(rideStatus, cancellationToken);
        RideStatuses.Add(rideStatus);
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

file sealed class TestRidePresence : IRideParticipantPresence { public bool Online; public bool IsForeground(int userId) => Online; }
