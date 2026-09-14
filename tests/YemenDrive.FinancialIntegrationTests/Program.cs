using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.DriverPayments;
using YemenDrive.Application.DriverSettlements;
using YemenDrive.Application.Accounting;
using DriverSettlementPaymentService = YemenDrive.Application.DriverSettlements.DriverSettlementPayment;
using YemenDrive.Application.Payments;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideService = YemenDrive.Application.Rides.Ride;
using LedgerAccountEntity = YemenDrive.Database.Entities.LedgerAccount;
using CashCollectionApprovalService = YemenDrive.Application.DriverPayments.CashCollectionApproval;

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

    await GeneralLedgerFoundationIsBalancedAndImmutableAsync(db, configuration, fixture);
    await WalletPaymentIsAtomicAndIdempotentAsync(db, configuration, fixture);
    await WalletPaymentCanBeRefundedWithCancellationFeeAsync(db, configuration, fixture);
    await CashSettlementHandlesExcessAndShortageAsync(db, configuration, fixture);
    await InsufficientCashShortageRollsBackAsync(db, configuration, fixture);

    Console.WriteLine("PASS: financial integration scenarios completed on an isolated temporary database.");
}

finally
{
    await using var cleanup = new YemenDriveDbContext(options);
    await cleanup.Database.EnsureDeletedAsync();
    if (File.Exists(settingsPath)) File.Delete(settingsPath);
}

static async Task GeneralLedgerFoundationIsBalancedAndImmutableAsync(
    YemenDriveDbContext db,
    DatabaseConfigurationStore configuration,
    Fixture fixture)
{
    var setup = new AccountingSetup(db, configuration, new TestCurrentUser(fixture.AdminId));
    AssertSuccess(await ExecuteAsync(setup, "add", new AccountingSetupModel()), "accounting chart setup");
    Assert(await db.LedgerAccounts.CountAsync() >= 6, "standard accounting accounts must exist");

    var driverParty = new FinancialParty { Code = "TEST-DRIVER", Name = "جهة اختبار السائق", Type = FinancialPartyType.User, EntityId = fixture.DriverId };
    var customerParty = new FinancialParty { Code = "TEST-CUSTOMER-WALLET", Name = "جهة اختبار محفظة العميل", Type = FinancialPartyType.Wallet, EntityId = fixture.CustomerWalletId };
    db.FinancialParties.AddRange(driverParty, customerParty);
    await db.SaveChangesAsync();
    var driverAccount = new LedgerAccountEntity { Code = "1100-T", Name = "حساب سائق اختبار", Type = LedgerAccountType.Asset, Purpose = LedgerAccountPurpose.DriverCurrentAccount, Currency = "YER", FinancialPartyId = driverParty.Id };
    var customerWalletAccount = new LedgerAccountEntity { Code = "2100-T", Name = "محفظة عميل اختبار", Type = LedgerAccountType.Liability, Purpose = LedgerAccountPurpose.CustomerWallet, Currency = "YER", FinancialPartyId = customerParty.Id };
    db.LedgerAccounts.AddRange(driverAccount, customerWalletAccount);
    await db.SaveChangesAsync();
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

    var customerWallet = new Wallet { UserId = customer.Id, Balance = 3_000m, Currency = "YER" };
    var driverWallet = new Wallet { UserId = driver.Id, Balance = 100m, Currency = "YER" };
    db.Wallets.AddRange(customerWallet, driverWallet);
    await db.SaveChangesAsync();
    return new Fixture(customer.Id, driver.Id, admin.Id, kind.Id, service.Id, customerWallet.Id, driverWallet.Id);
}

static async Task WalletPaymentIsAtomicAndIdempotentAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var ride = await AddRideAsync(db, fixture, RideStatus.Completed, 900m, 100m, 50m);
    var payment = new Payment(db, configuration, new TestCurrentUser(fixture.CustomerId));
    var request = new PaymentModel(ride.Id, ride.Id, null, 1_000m, "YER", "YemenDriveWallet", PaymentStatus.Paid, null, "wallet-payment-idempotency");
    AssertSuccess(await ExecuteAsync(payment, "add", request), "wallet payment");
    AssertSuccess(await ExecuteAsync(payment, "add", request), "wallet payment retry");
    db.ChangeTracker.Clear();
    AssertEqual(2_000m, await WalletBalanceAsync(db, fixture.CustomerId), "customer wallet after payment");
    AssertEqual(950m, await WalletBalanceAsync(db, fixture.DriverId), "driver wallet after payment");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid), "paid payment count");
    AssertEqual(2, await db.WalletTransactions.CountAsync(x => x.RideId == ride.Id), "wallet movements count");
}

static async Task WalletPaymentCanBeRefundedWithCancellationFeeAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var ride = await db.Rides.OrderBy(x => x.Id).FirstAsync(x => x.Status == RideStatus.Completed);
    var cancellation = new RideService(db, configuration, new TestCurrentUser(fixture.CustomerId));
    AssertSuccess(await ExecuteAsync(cancellation, "cancel", new RideModel(Id: ride.Id)), "wallet cancellation refund");
    db.ChangeTracker.Clear();
    AssertEqual(2_950m, await WalletBalanceAsync(db, fixture.CustomerId), "customer wallet after refund");
    AssertEqual(100m, await WalletBalanceAsync(db, fixture.DriverId), "driver wallet after reversal");
    AssertEqual(1, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Refunded), "refund record count");
    AssertEqual(RideStatus.Cancelled, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "ride status after refund");
}

static async Task CashSettlementHandlesExcessAndShortageAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var excessRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId));
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(excessRide.Id, 1_100m, "YER", null, "cash-excess")), "cash excess");
    db.ChangeTracker.Clear();
    AssertEqual(3_050m, await WalletBalanceAsync(db, fixture.CustomerId), "customer wallet after cash excess");
    AssertEqual(PaymentStatus.Pending, await db.DriverSettlements.Where(x => x.PaymentReference!.Contains($"ride:{excessRide.Id}:" )).Select(x => x.Status).SingleAsync(), "cash platform debt status");
    var debt = await db.DriverSettlements.SingleAsync(x => x.PaymentReference!.Contains($"ride:{excessRide.Id}:"));
    var originalNetPayable = debt.NetPayable;
    var settlementPayment = new DriverSettlementPaymentService(db, configuration, new TestCurrentUser(fixture.AdminId));
    var collection = new DriverSettlementPaymentModel(debt.Id, debt.Id, -debt.NetPayable, "YER", "CashToPlatform", "cash-debt-collection", "اختبار تحصيل");
    AssertSuccess(await ExecuteAsync(settlementPayment, "add", collection), "cash debt collection");
    Assert(! (await ExecuteAsync(settlementPayment, "add", collection)).Success, "duplicate collection reference must be rejected");
    db.ChangeTracker.Clear();
    AssertEqual(originalNetPayable, await db.DriverSettlements.Where(x => x.Id == debt.Id).Select(x => x.NetPayable).SingleAsync(), "source debt remains immutable");
    AssertEqual(-originalNetPayable, await db.DriverSettlementPayments.Where(x => x.DriverSettlementId == debt.Id).SumAsync(x => x.Amount), "cash debt collected amount");

    var shortageRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(shortageRide.Id, 900m, "YER", null, "cash-shortage")), "cash shortage approval request");
    db.ChangeTracker.Clear();
    var approval = await db.CashCollectionApprovals.SingleAsync(x => x.RideId == shortageRide.Id);
    AssertEqual(CashCollectionApprovalStatus.Pending, approval.Status, "cash shortage starts pending customer approval");
    AssertEqual(3_050m, await WalletBalanceAsync(db, fixture.CustomerId), "wallet stays unchanged before customer approval");
    AssertEqual(0, await db.PaymentTransactions.CountAsync(x => x.RideId == shortageRide.Id), "no payment before customer approval");

    var customerApproval = new CashCollectionApprovalService(db, configuration, new TestCurrentUser(fixture.CustomerId));
    AssertSuccess(await ExecuteAsync(customerApproval, "accept", new CashCollectionApprovalModel(approval.Id)), "customer accepts wallet shortfall");
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(shortageRide.Id, 900m, "YER", null, "cash-shortage-complete")), "cash shortage after approval");
    db.ChangeTracker.Clear();
    AssertEqual(2_950m, await WalletBalanceAsync(db, fixture.CustomerId), "customer wallet after cash shortage");
}

static async Task InsufficientCashShortageRollsBackAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var wallet = await db.Wallets.SingleAsync(x => x.UserId == fixture.CustomerId);
    wallet.Balance = 50m;
    await db.SaveChangesAsync();
    var ride = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId));
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 800m, "YER", null, "cash-insufficient")), "insufficient shortage request");
    db.ChangeTracker.Clear();
    AssertEqual(0, await db.CashCollectionApprovals.CountAsync(x => x.RideId == ride.Id), "insufficient shortage creates no approval request");
    Assert(await db.Notifications.AnyAsync(x => x.UserId == fixture.CustomerId && x.Body.Contains("لا يكفي")), "customer receives insufficient-wallet notification");
    AssertEqual(50m, await WalletBalanceAsync(db, fixture.CustomerId), "wallet unchanged after rejected cash shortage");
    AssertEqual(0, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id), "no payment after rejected cash shortage");
    AssertEqual(RideStatus.InProgress, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "ride remains in progress after rejected cash shortage");
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

static void AssertSuccess(ApiResult result, string name) => Assert(result.Success, $"{name} failed: {result.Code}");
static void AssertEqual<T>(T expected, T actual, string name) where T : notnull => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"{name}: expected {expected}, got {actual}");
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed record Fixture(int CustomerId, int DriverId, int AdminId, int ServiceKindId, int ServiceCatalogItemId, int CustomerWalletId, int DriverWalletId);
file sealed class TestCurrentUser(int userId) : ICurrentUserContext
{
    public int? UserId => userId;
}
