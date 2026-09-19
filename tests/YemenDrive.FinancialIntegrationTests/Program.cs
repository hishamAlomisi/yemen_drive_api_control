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

    await FinancialAccountsAreProvisionedForUsersAndServiceKindsAsync(db, configuration, fixture);
    await GeneralLedgerFoundationIsBalancedAndImmutableAsync(db, configuration, fixture);
    await WalletPaymentIsAtomicAndIdempotentAsync(db, configuration, fixture);
    await RideCancellationRequiresReasonAndFollowsReviewWorkflowAsync(db, configuration, fixture);
    await CashSettlementHandlesExcessAndShortageAsync(db, configuration, fixture);
    await CustomerCashPaymentRequiresDriverConfirmationAsync(db, configuration, fixture);
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

    var customerWallet = new Wallet { UserId = customer.Id, Balance = 3_000m, Currency = "YER" };
    var driverWallet = new Wallet { UserId = driver.Id, Balance = 100m, Currency = "YER" };
    db.Wallets.AddRange(customerWallet, driverWallet);
    await db.SaveChangesAsync();
    return new Fixture(customer.Id, driver.Id, admin.Id, kind.Id, service.Id, customerWallet.Id, driverWallet.Id);
}

static async Task WalletPaymentIsAtomicAndIdempotentAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var ride = await AddRideAsync(db, fixture, RideStatus.Completed, 900m, 100m, 50m);
    var payment = new Payment(db, configuration, new TestCurrentUser(fixture.CustomerId), RideAccounting(db));
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
}

static async Task RideCancellationRequiresReasonAndFollowsReviewWorkflowAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var completedRide = await db.Rides.OrderBy(x => x.Id).FirstAsync(x => x.Status == RideStatus.Completed);
    var customerCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.CustomerId));
    Assert(!(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: completedRide.Id, Reason: "لا أريد المتابعة"))).Success,
        "completed ride cannot be cancelled or refunded");

    var ride = await AddRideAsync(db, fixture, RideStatus.DriverEnRoute, 900m, 100m, 50m);
    Assert(!(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: " "))).Success,
        "cancellation reason is mandatory");
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: "تغيرت خططي اليوم")), "customer cancellation request");
    db.ChangeTracker.Clear();
    var first = await db.RideCancellationRequests.SingleAsync(x => x.RideId == ride.Id);
    AssertEqual(RideCancellationStatus.DriverReviewPending, first.Status, "pre-start cancellation is sent to driver");
    AssertEqual(RideStatus.CancellationPending, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "ride stops while cancellation is reviewed");
    var driverCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.DriverId));
    AssertSuccess(await ExecuteAsync(driverCancellation, "reject", new RideCancellationRequestModel(Id: first.Id, Note: "لا أوافق")), "driver rejects cancellation");
    db.ChangeTracker.Clear();
    AssertEqual(RideCancellationStatus.DriverRejected, await db.RideCancellationRequests.Where(x => x.Id == first.Id).Select(x => x.Status).SingleAsync(), "driver rejection is recorded");
    AssertSuccess(await ExecuteAsync(customerCancellation, "add", new RideCancellationRequestModel(RideId: ride.Id, Reason: "ما زلت بحاجة للإلغاء")), "customer retries cancellation");
    var second = await db.RideCancellationRequests.OrderByDescending(x => x.Id).FirstAsync(x => x.RideId == ride.Id);
    AssertSuccess(await ExecuteAsync(driverCancellation, "refer", new RideCancellationRequestModel(Id: second.Id, Note: "مراجعة الإدارة مطلوبة")), "driver refers cancellation to administration");
    db.ChangeTracker.Clear();
    AssertEqual(RideCancellationStatus.AdminReviewPending, await db.RideCancellationRequests.Where(x => x.Id == second.Id).Select(x => x.Status).SingleAsync(), "referred cancellation awaits administration");
    var adminCancellation = new RideCancellationRequestService(db, configuration, new TestCurrentUser(fixture.AdminId));
    AssertSuccess(await ExecuteAsync(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: second.Id, Note: "تمت المراجعة")), "admin approves cancellation");
    AssertEqual(RideStatus.Cancelled, await db.Rides.Where(x => x.Id == ride.Id).Select(x => x.Status).SingleAsync(), "admin approval finalizes cancellation");
}

static async Task CashSettlementHandlesExcessAndShortageAsync(YemenDriveDbContext db, DatabaseConfigurationStore configuration, Fixture fixture)
{
    var excessRide = await AddRideAsync(db, fixture, RideStatus.InProgress, 900m, 100m, 0m);
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
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

    var customerApproval = new CashCollectionApprovalService(db, configuration, new TestCurrentUser(fixture.CustomerId));
    AssertSuccess(await ExecuteAsync(customerApproval, "accept", new CashCollectionApprovalModel(approval.Id)), "customer accepts wallet shortfall");
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(shortageRide.Id, 900m, "YER", null, "cash-shortage-complete")), "cash shortage after approval");
    db.ChangeTracker.Clear();
    AssertEqual(2_000m, await WalletBalanceAsync(db, fixture.CustomerId), "customer wallet after cash shortage");
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
    var customerRequest = new CashPaymentRequestService(db, configuration, new TestCurrentUser(fixture.CustomerId));
    var driverRequest = new CashPaymentRequestService(db, configuration, new TestCurrentUser(fixture.DriverId));
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));

    AssertSuccess(await ExecuteAsync(customerRequest, "add", new CashPaymentRequestModel(RideId: ride.Id, IdempotencyKey: "cash-customer-reject")), "customer asks for cash confirmation");
    var rejected = await db.CashPaymentRequests.OrderByDescending(x => x.Id).FirstAsync(x => x.RideId == ride.Id);
    AssertSuccess(await ExecuteAsync(driverRequest, "reject", new CashPaymentRequestModel(Id: rejected.Id)), "driver rejects unreceived cash");
    AssertEqual(CashPaymentRequestStatus.DriverRejected, await db.CashPaymentRequests.Where(x => x.Id == rejected.Id).Select(x => x.Status).SingleAsync(), "rejected cash request is recorded");
    Assert(await db.Notifications.AnyAsync(x => x.UserId == fixture.CustomerId && x.Body.Contains("لم يستلم")), "customer is notified of cash rejection");

    AssertSuccess(await ExecuteAsync(customerRequest, "add", new CashPaymentRequestModel(RideId: ride.Id, IdempotencyKey: "cash-customer-confirm")), "customer retries cash confirmation");
    var confirmed = await db.CashPaymentRequests.OrderByDescending(x => x.Id).FirstAsync(x => x.RideId == ride.Id);
    AssertSuccess(await ExecuteAsync(driverRequest, "accept", new CashPaymentRequestModel(Id: confirmed.Id)), "driver confirms received cash");
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 1_000m, "YER", null, "cash-customer-confirmed")), "driver records confirmed customer cash");
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
    var cash = new DriverCashPayment(db, configuration, new TestCurrentUser(fixture.DriverId), RideAccounting(db));
    AssertSuccess(await ExecuteAsync(cash, "add", new DriverCashPaymentModel(ride.Id, 800m, "YER", null, "cash-insufficient")), "insufficient shortage request");
    db.ChangeTracker.Clear();
    AssertEqual(0, await db.CashCollectionApprovals.CountAsync(x => x.RideId == ride.Id), "insufficient shortage creates no approval request");
    Assert(await db.Notifications.AnyAsync(x => x.UserId == fixture.CustomerId && x.Body.Contains("لا يكفي")), "customer receives insufficient-wallet notification");
    AssertEqual(50m, await WalletBalanceAsync(db, fixture.CustomerId), "wallet unchanged after rejected cash shortage");
    AssertEqual(0, await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id), "no payment after rejected cash shortage");
    AssertEqual(0, await db.JournalEntries.CountAsync(x => x.SourceType == "RideCashPayment" && x.SourceId == $"cash:ride:{ride.Id}:driver:{fixture.DriverId}"), "no journal after rejected cash shortage");
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

static RideAccountingPostingService RideAccounting(YemenDriveDbContext db) =>
    new(db, new AccountingPostingService(db));

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
