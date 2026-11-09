using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Administration;
using YemenDrive.Application.DriverPayments;
using YemenDrive.Application.Maps;
using YemenDrive.Application.Payments;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using RideEntity = YemenDrive.Database.Entities.Ride;
using CancellationService = YemenDrive.Application.Rides.RideCancellationRequest;

RealtimeScenarioMode.Enabled = args.Contains("--journal");
Console.WriteLine($"MODE: durable journal = {RealtimeScenarioMode.Enabled}");
var name = "YemenDrive_OperationalTests_" + Guid.NewGuid().ToString("N");
var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={name};Integrated Security=True;TrustServerCertificate=True").Options;
var settingsFile = Path.Combine(Path.GetTempPath(), name + ".json");
var config = new DatabaseConfigurationStore(settingsFile);
await config.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = name, IntegratedSecurity = true, TrustServerCertificate = true }, default);
try
{
    await using var db = new YemenDriveDbContext(options, RealtimeScenarioMode.Settings);
    await db.Database.MigrateAsync();
    Is(!db.Database.HasPendingModelChanges(), "migration snapshot matches the final model");
    var customer = new User { PhoneNumber = "967700110001", DisplayName = "Test customer", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "967700110002", DisplayName = "Test driver", Role = UserRole.Driver };
    var admin = new User { PhoneNumber = "967700110003", DisplayName = "Test admin", Role = UserRole.Admin };
    var kind = new ServiceKind { Code = "ops", NameAr = "اختبار" };
    db.AddRange(customer, driver, admin, kind); await db.SaveChangesAsync();
    var service = new RideServiceCatalogItem { Code = "ops-car", NameAr = "مركبة اختبار", DescriptionAr = "اختبار", ServiceKindId = kind.Id };
    db.Add(service); await db.SaveChangesAsync();
    db.AddRange(new Wallet { UserId = customer.Id, Currency = "YER", Balance = 10000 }, new Wallet { UserId = driver.Id, Currency = "YER", Balance = 0 });
    db.DriverProfiles.Add(new DriverProfile { UserId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id, VehicleModel = "Test", PlateNumber = "TEST", ApprovedPassengerSeats = 4 });
    db.AdministratorPermissions.Add(new() { UserId = admin.Id, Permission = "*" }); await db.SaveChangesAsync();
    await new FinancialAccountProvisioningService(db).ProvisionExistingAsync(default);
    await WalletTransferScenarios.RunAsync(db, config, admin.Id);
    await SavedPlaceScenarios.RunAsync(db, config);
    await WalletLedgerScenarios.RunAsync(db, config, admin.Id);
    var accounting = new RideAccountingPostingService(db, new(db));
    await AdministrationTrackingScenarios.RunAsync(db, config, driver.Id, admin.Id);
    var actorCustomer = new Actor(customer.Id); var actorDriver = new Actor(driver.Id); var actorAdmin = new Actor(admin.Id);
    var cash = new DriverCashPayment(db, config, actorDriver, accounting);
    var wallet = new Payment(db, config, actorCustomer, accounting);
    var serviceEnd = new RideServiceControl(db, config, actorDriver);
    var cases = new RideFinancialCaseService(db, config, actorCustomer);
    var riderCancellation = new CancellationService(db, config, actorCustomer, accounting);
    var driverCancellation = new CancellationService(db, config, actorDriver, accounting);
    var adminCancellation = new CancellationService(db, config, actorAdmin, accounting);
    async Task<RideEntity> Trip(RideStatus status = RideStatus.InProgress)
    {
        var ride = new RideEntity { CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind.Id, ServiceCatalogItemId = service.Id, Status = status, CustomerPrice = 900, ServerPrice = 900, TotalAmount = 1000, ServiceFee = 100, DriverShare = 850, DriverCommissionAmount = 50, AppliedDriverCommissionFixed = 50, StartedAtUtc = status == RideStatus.InProgress ? DateTime.UtcNow : null };
        db.Rides.Add(ride); await db.SaveChangesAsync(); return ride;
    }
    var partial = await Trip();
    Ok(await Run(cash, "add", new DriverCashPaymentModel(partial.Id, 400, "YER", IdempotencyKey: "partial-one")));
    Ok(await Run(serviceEnd, "finish", new RideServiceModel(partial.Id, "وصل الراكب وانتهى النقل عند نقطة آمنة")));
    var finance = await RideFinancialState.ReadAsync(db, partial, default);
    Is(partial.ServiceEndedAtUtc != null && partial.Status == RideStatus.Completed && finance.OutstandingAmount == 600 && !finance.PaymentCompleted, "physical service ends with verified outstanding cash");
    var dueCase = await db.RideFinancialCases.SingleAsync(x => x.RideId == partial.Id && x.Kind == "Receivable");
    Is(dueCase.AccruedReceivable == 600 && dueCase.AccruedDriverEntitlement == 510, "uncollected driver entitlement is independent of wallet");
    var debtWallet = await Run(new YemenDrive.Application.Wallets.Wallet(db, config, actorCustomer), "get", new YemenDrive.Application.Wallets.WalletModel());
    Ok(debtWallet);
    Is(JsonSerializer.SerializeToElement(debtWallet.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("outstandingRideDebt").GetDecimal() == 600, "wallet exposes outstanding ride debt without fabricating payment");
    Is(await db.Wallets.Where(x => x.UserId == driver.Id).Select(x => x.Balance).SingleAsync() == 0, "uncollected earnings are not withdrawable");
    var blocked = false; try { await RideFinancialState.EnsureNoConfirmedDebtAsync(db, customer.Id, default); } catch (ServiceException) { blocked = true; }
    Is(blocked, "only established open debt blocks new work");
    Ok(await Run(cases, "add", new RideFinancialCaseModel(RideId: partial.Id, Note: "اعتراض على الجزء غير المقبوض")));
    await RideFinancialState.EnsureNoConfirmedDebtAsync(db, customer.Id, default);
    Is(dueCase.Status == "Disputed", "open dispute does not imply confirmed debt");
    Ok(await Run(wallet, "add", new PaymentModel(RideId: partial.Id, Amount: 600, Provider: "YemenDriveWallet", IdempotencyKey: "settle-partial")));
    finance = await RideFinancialState.ReadAsync(db, partial, default);
    Is(finance.PaymentCompleted && dueCase.AccruedReceivable == 0 && dueCase.AccruedDriverEntitlement == 0, "later receipt clears accrued debt and pending entitlement");
    Ok(await Run(wallet, "add", new PaymentModel(RideId: partial.Id, Amount: 600, Provider: "YemenDriveWallet", IdempotencyKey: "settle-partial")));
    Is(await db.PaymentTransactions.CountAsync(x => x.RideId == partial.Id && x.Status == PaymentStatus.Paid) == 2, "partial cash and remainder each recorded once");
    var excess = await Trip();
    var customerBefore = await db.Wallets.Where(x => x.UserId == customer.Id).Select(x => x.Balance).SingleAsync();
    Ok(await Run(cash, "add", new DriverCashPaymentModel(excess.Id, 1200, "YER", IdempotencyKey: "change-one")));
    var change = await db.RideFinancialCases.SingleAsync(x => x.RideId == excess.Id && x.Kind == "CashChange");
    Is(await db.Wallets.Where(x => x.UserId == customer.Id).Select(x => x.Balance).SingleAsync() == customerBefore && change.Amount == 200, "cash change requires explicit customer choice");
    var driverCases = new RideFinancialCaseService(db, config, actorDriver);
    Ok(await Run(driverCases, "update", new RideFinancialCaseModel(Id: change.Id, Resolution: "CashReturned")));
    Is(change.Status == "AwaitingCashReceipt", "driver statement alone does not settle cash change");
    Ok(await Run(cases, "reject", new RideFinancialCaseModel(Id: change.Id, Note: "لم أستلم الباقي")));
    Is(change.Status == "ManualReview" && change.AssignedAdminId == admin.Id, "first cash receipt disagreement is owned by administration");
    var cancelled = await Trip(RideStatus.DriverArrived);
    Ok(await Run(riderCancellation, "add", new RideCancellationRequestModel(RideId: cancelled.Id, Reason: "تأخر السائق ولا أريد المتابعة")));
    var cancellation = await db.RideCancellationRequests.SingleAsync(x => x.RideId == cancelled.Id);
    Is(cancelled.Status == RideStatus.Cancelled && cancelled.ServiceEndedAtUtc != null, "prestart cancellation releases transport immediately");
    Ok(await Run(driverCancellation, "reject", new RideCancellationRequestModel(Id: cancellation.Id, Note: "اعتراض على السبب")));
    Is(cancellation.Status == RideCancellationStatus.AdminReviewPending, "first driver refusal reaches administrative review");
    Ok(await Run(adminCancellation, "adminReject", new RideCancellationRequestModel(Id: cancellation.Id, Note: "نراجع المسؤولية والأدلة")));
    Is(cancelled.Status == RideStatus.Cancelled && cancellation.Status == RideCancellationStatus.FinancialReviewPending, "administrative rejection cannot revive ended transport");
    var boarded = await Trip(RideStatus.DriverArrived);
    var rideService = new YemenDrive.Application.Rides.Ride(db, config, actorDriver, accounting);
    Is(!(await Run(rideService, "update", new RideModel(Id: boarded.Id, Status: RideStatus.InProgress))).Success, "start requires rider or explicit driver boarding confirmation");
    Ok(await Run(new RideServiceControl(db, config, actorCustomer), "board", new RideServiceModel(boarded.Id)));
    Ok(await Run(rideService, "update", new RideModel(Id: boarded.Id, Status: RideStatus.InProgress)));
    var driverBoarded = await Trip(RideStatus.DriverArrived);
    Ok(await Run(rideService, "update", new RideModel(Id: driverBoarded.Id, Status: RideStatus.InProgress, ConfirmPassengerBoarding: true)));
    Is(driverBoarded.PassengerBoardedAtUtc != null && driverBoarded.Status == RideStatus.InProgress, "driver confirmation records boarding and starts atomically");
    var unpaidBoarding = await Trip(RideStatus.DriverArrived);
    unpaidBoarding.PaymentMethodCode = "wallet";
    await db.SaveChangesAsync();
    Is(!(await Run(rideService, "update", new RideModel(Id: unpaidBoarding.Id, Status: RideStatus.InProgress, ConfirmPassengerBoarding: true))).Success, "driver boarding confirmation cannot bypass payment");
    Is(unpaidBoarding.PassengerBoardedAtUtc == null && unpaidBoarding.Status == RideStatus.DriverArrived, "failed start does not falsely record boarding");
    var readiness = new YemenDrive.Application.Drivers.DriverReadiness(db, config, actorDriver);
    Ok(await Run(readiness, "update", new YemenDrive.Application.Drivers.DriverReadinessModel(false)));
    Ok(await Run(serviceEnd, "finish", new RideServiceModel(boarded.Id, "انتهت الرحلة عند الوجهة")));
    Is(!await db.DriverProfiles.Where(x => x.UserId == driver.Id).Select(x => x.AcceptsNewRequests).SingleAsync(), "transport completion preserves driver offline choice");
    // The quote is already persisted from the real route preview; exercise driver acceptance and payment.
    var prepaid = await Trip(); prepaid.PaymentMethodCode = "wallet"; prepaid.CustomerPaymentEnabled = true; await db.SaveChangesAsync();
    Ok(await Run(wallet, "add", new PaymentModel(RideId: prepaid.Id, Amount: 1000, Provider: "YemenDriveWallet", IdempotencyKey: "prepay-one")));
    prepaid.HasRoutePricingSnapshot = true; prepaid.DestinationLabel = "الوجهة الحالية"; await db.SaveChangesAsync();
    var now = DateTime.UtcNow;
    db.DriverLiveLocations.Add(new() { DriverId = driver.Id, Latitude = 15, Longitude = 44, IsOnline = true, ObservedAtUtc = now });
    var route = new RideRouteChange { RideId = prepaid.Id, RequestKey = Guid.NewGuid(), RequestedByUserId = customer.Id, Status = "pending", ExpiresAtUtc = now.AddMinutes(2), ChangeObservedAtUtc = now, ChangeLatitude = 15, ChangeLongitude = 44, PreviousTotal = 1000, NewTotal = 1200, NewRouteFare = 1100, OriginalServiceFee = 100, NewDestinationLabel = "وجهة أغلى", NewDestinationLatitude = 15.01, NewDestinationLongitude = 44.01 };
    db.Add(route); await db.SaveChangesAsync();
    var maps = new GoogleMapsGateway(new HttpClient(new NoNetwork()), new GoogleMapsOptions { RoutesApiKey = "local-test" }, NullLogger<GoogleMapsGateway>.Instance);
    Ok(await Run(new RideRouteChangeService(db, config, actorDriver, maps), "accept", new RideRouteChangeModel(prepaid.Id, route.Id)));
    Is(route.Status == "accepted" && prepaid.DestinationLabel == "وجهة أغلى" && (await RideFinancialState.ReadAsync(db, prepaid, default)).OutstandingAmount == 200, "prepaid increase applies approved route and bills only the difference");
    Ok(await Run(wallet, "add", new PaymentModel(RideId: prepaid.Id, Amount: 200, Provider: "YemenDriveWallet", IdempotencyKey: "route-difference")));
    Is(route.Status == "accepted" && prepaid.TotalAmount == 1200 && prepaid.DestinationLabel == "وجهة أغلى", "confirmed difference applies agreed route once");
    Is(await db.PaymentTransactions.Where(x => x.RideId == prepaid.Id && x.Status == PaymentStatus.Paid).SumAsync(x => x.ServiceFeeAmount) == 100, "service fee is not charged again on prepaid adjustment");
    Is(await db.PaymentTransactions.Where(x => x.RideId == prepaid.Id && x.Status == PaymentStatus.Paid).SumAsync(x => x.DriverNetAmount) == 1050, "driver entitlement reconciles after fare increase");
    var decrease = new RideRouteChange { RideId = prepaid.Id, RequestKey = Guid.NewGuid(), RequestedByUserId = customer.Id, Status = "pending", RouteVersion = prepaid.RouteVersion, ExpiresAtUtc = now.AddMinutes(2), ChangeObservedAtUtc = now, ChangeLatitude = 15, ChangeLongitude = 44, PreviousTotal = 1200, NewTotal = 700, NewRouteFare = 600, OriginalServiceFee = 100, NewDestinationLabel = "وجهة أقرب", NewDestinationLatitude = 15.002, NewDestinationLongitude = 44.002 };
    db.Add(decrease); await db.SaveChangesAsync();
    Ok(await Run(new RideRouteChangeService(db, config, actorDriver, maps), "accept", new RideRouteChangeModel(prepaid.Id, decrease.Id)));
    var decreaseCase = await db.RideFinancialCases.SingleAsync(x => x.RideId == prepaid.Id && x.Kind == "FareDecrease");
    Is(prepaid.TotalAmount == 700 && decreaseCase.Amount == 500, "prepaid decrease preserves exact refund entitlement");
    Ok(await Run(cases, "accept", new RideFinancialCaseModel(Id: decreaseCase.Id)));
    Is((await RideFinancialState.ReadAsync(db, prepaid, default)).RefundedAmount == 500 && prepaid.CollectedDriverEntitlement == 550, "authorized decrease refunds difference and reverses only unearned driver share");
    Is(!(await Run(cases, "accept", new RideFinancialCaseModel(Id: decreaseCase.Id))).Success, "stale refund approval cannot refund twice");
    var paidCash = await Trip();
    Ok(await Run(cash, "add", new DriverCashPaymentModel(paidCash.Id, 1000, "YER", IdempotencyKey: "cancel-paid-cash")));
    paidCash.Status = RideStatus.DriverArrived; paidCash.StartedAtUtc = null; await db.SaveChangesAsync();
    Ok(await Run(riderCancellation, "add", new RideCancellationRequestModel(RideId: paidCash.Id, Reason: "لم أعد بحاجة للرحلة")));
    var paidCancellation = await db.RideCancellationRequests.SingleAsync(x => x.RideId == paidCash.Id);
    Ok(await Run(driverCancellation, "accept", new RideCancellationRequestModel(Id: paidCancellation.Id)));
    Ok(await Run(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: paidCancellation.Id, Note: "رسوم الخدمة من السياسة الموثقة، ولم يبدأ النقل")));
    Ok(await Run(driverCancellation, "update", new RideCancellationRequestModel(Id: paidCancellation.Id, RequestedRefundMethod: CashCancellationRefundMethod.ReturnFromDriver, RefundChoiceRevision: 0, CashReturned: true)));
    Is(!await db.PaymentTransactions.AnyAsync(x => x.RideId == paidCash.Id && x.Status == PaymentStatus.Refunded) && paidCash.Status == RideStatus.Cancelled, "cash return claim leaves refund unconfirmed and transport released");
    Ok(await Run(riderCancellation, "reject", new RideCancellationRequestModel(Id: paidCancellation.Id, RefundChoiceRevision: 1)));
    Is(paidCancellation.Status == RideCancellationStatus.FinancialReviewPending, "cash refund dispute has a direct review path");
    Ok(await Run(adminCancellation, "adminApprove", new RideCancellationRequestModel(Id: paidCancellation.Id, Note: "تحققنا من إيصال استلام العميل", CashReceiptConfirmed: true, EvidenceReference: "isolated-test-customer-receipt")));
    Is(await db.PaymentTransactions.Where(x => x.RideId == paidCash.Id && x.Status == PaymentStatus.Refunded).SumAsync(x => x.Amount) == 900, "reviewed cash receipt confirms only net refund once");
    var support = new YemenDrive.Application.Support.SupportTicket(db, config, actorCustomer);
    Ok(await Run(support, "add", new YemenDrive.Application.Support.SupportTicketModel(Message: "اعتراض على رحلة", RideId: cancelled.Id)));
    var ticket = await db.SupportTickets.SingleAsync();
    Is(!(await Run(support, "update", new YemenDrive.Application.Support.SupportTicketModel(Id: ticket.Id, Status: "Resolved", AdminReply: "تغيير غير مصرح"))).Success, "customer cannot author administrative support decision");
    await ExtendedScenarios.RunAsync(db, config, customer.Id, driver.Id, admin.Id, kind.Id, service.Id);
    var reassign = new RideFinancialCase { RideId = partial.Id, CustomerId = customer.Id, DriverId = driver.Id, Kind = "Dispute", Status = "ManualReview", Amount = 25, SourceReference = "owner-reassignment", AssignedAdminId = driver.Id };
    db.RideFinancialCases.Add(reassign); await db.SaveChangesAsync();
    await OperationalReviewCoordinator.ReconcileAsync(db, default);
    Is(await db.RideFinancialCases.Where(x => x.SourceReference == "owner-reassignment").Select(x => x.AssignedAdminId).SingleAsync() == admin.Id, "unqualified review owner is replaced by an active authorized administrator");
    var supplemental = await Trip();
    supplemental.DriverShare = 900; supplemental.DriverCommissionAmount = 0; supplemental.AppliedDriverCommissionFixed = 0;
    supplemental.PaymentMethodCode = "wallet"; supplemental.CustomerPaymentEnabled = true; supplemental.HasRoutePricingSnapshot = true;
    await db.SaveChangesAsync();
    Ok(await Run(wallet, "add", new PaymentModel(RideId: supplemental.Id, Amount: 1000, Provider: "YemenDriveWallet", IdempotencyKey: "supplement-original")));
    var liveForSupplement = await db.DriverLiveLocations.SingleAsync(x => x.DriverId == driver.Id);
    liveForSupplement.Latitude = 15; liveForSupplement.Longitude = 44; liveForSupplement.ObservedAtUtc = DateTime.UtcNow; liveForSupplement.IsOnline = true;
    var supplementChange = new RideRouteChange { RideId = supplemental.Id, RequestKey = Guid.NewGuid(), RequestedByUserId = customer.Id, Status = "pending", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(2), ChangeObservedAtUtc = liveForSupplement.ObservedAtUtc, ChangeLatitude = 15, ChangeLongitude = 44, PreviousTotal = 1000, NewTotal = 1300, NewRouteFare = 1200, OriginalServiceFee = 100, NewDestinationLabel = "وجهة بعد الدفع", NewDestinationLatitude = 15.01, NewDestinationLongitude = 44.01 };
    db.RideRouteChanges.Add(supplementChange); await db.SaveChangesAsync();
    Ok(await Run(new RideRouteChangeService(db, config, actorDriver, maps), "accept", new RideRouteChangeModel(supplemental.Id, supplementChange.Id)));
    Is(supplementChange.Status == "accepted" && supplemental.CustomerPaymentEnabled && (await RideFinancialState.ReadAsync(db, supplemental, default)).OutstandingAmount == 300, "paid route applies and exposes only 300 supplemental balance");
    Ok(await Run(new YemenDrive.Application.Rides.Ride(db, config, actorCustomer, accounting), "update", new RideModel(Id: supplemental.Id, PaymentMethodCode: "cash")));
    Ok(await Run(cash, "add", new DriverCashPaymentModel(supplemental.Id, 100, "YER", IdempotencyKey: "supplement-cash")));
    Ok(await Run(serviceEnd, "finish", new RideServiceModel(supplemental.Id, "انتهى النقل والمتبقي مسجل للمتابعة")));
    Is(await db.RideFinancialCases.AnyAsync(x => x.RideId == supplemental.Id && x.Kind == "Receivable" && x.Amount == 200 && x.Status == "Open"), "partial cash leaves only 200 verified debt after service end");
    Ok(await Run(wallet, "add", new PaymentModel(RideId: supplemental.Id, Amount: 200, Provider: "YemenDriveWallet", IdempotencyKey: "supplement-remainder")));
    Is((await RideFinancialState.ReadAsync(db, supplemental, default)).OutstandingAmount == 0 && await db.RideFinancialCases.AnyAsync(x => x.RideId == supplemental.Id && x.Kind == "Receivable" && x.Status == "Resolved"), "later wallet payment clears debt without recollecting original fare");
    Is(await db.PaymentTransactions.Where(x => x.RideId == supplemental.Id && x.Status == PaymentStatus.Paid).SumAsync(x => x.ServiceFeeAmount) == 100, "mixed supplemental payments retain one service fee");
    foreach (var originalCash in new[] { true, false })
    foreach (var returnCash in new[] { true, false })
    {
        var matrix = await Trip();
        matrix.DriverShare = 900; matrix.DriverCommissionAmount = 0; matrix.AppliedDriverCommissionFixed = 0;
        matrix.PaymentMethodCode = originalCash ? "cash" : "wallet"; matrix.CustomerPaymentEnabled = true;
        await db.SaveChangesAsync();
        var driverBefore = await db.Wallets.Where(x => x.UserId == driver.Id).Select(x => x.Balance).SingleAsync();
        var riderBefore = await db.Wallets.Where(x => x.UserId == customer.Id).Select(x => x.Balance).SingleAsync();
        var key = $"refund-matrix-{originalCash}-{returnCash}";
        if (originalCash) Ok(await Run(cash, "add", new DriverCashPaymentModel(matrix.Id, 1000, "YER", IdempotencyKey: key)));
        else Ok(await Run(wallet, "add", new PaymentModel(RideId: matrix.Id, Amount: 1000, Provider: "YemenDriveWallet", IdempotencyKey: key)));
        var originalDriverLines = await db.JournalLines.Where(x => x.RideId == matrix.Id && x.LedgerAccount.Purpose == LedgerAccountPurpose.DriverCurrentAccount).ToListAsync();
        Is(originalCash ? originalDriverLines.Count == 1 && originalDriverLines[0].Debit == 100 && originalDriverLines[0].Credit == 0 : originalDriverLines.Count == 0, key + ": cash posts only fees to driver current account");
        matrix.TotalAmount = 100; matrix.CustomerPrice = 0; matrix.DriverShare = 0;
        var refundCase = new RideFinancialCase { Ride = matrix, RideId = matrix.Id, CustomerId = customer.Id, DriverId = driver.Id, Kind = "FareDecrease", Status = "AwaitingConsent", Amount = 900, SourceReference = key };
        db.RideFinancialCases.Add(refundCase); await db.SaveChangesAsync();
        if (returnCash)
        {
            Ok(await Run(cases, "update", new RideFinancialCaseModel(Id: refundCase.Id, Resolution: "RequestCash")));
            Is(!(await Run(cases, "accept", new RideFinancialCaseModel(Id: refundCase.Id))).Success, key + ": cash cannot be confirmed before driver returns it");
            Ok(await Run(driverCases, "update", new RideFinancialCaseModel(Id: refundCase.Id, Resolution: "CashReturned")));
            Is(!(await db.PaymentTransactions.AnyAsync(x => x.RideId == matrix.Id && x.Status == PaymentStatus.Refunded)), key + ": driver cash statement alone cannot post refund");
        }
        Ok(await Run(cases, "accept", new RideFinancialCaseModel(Id: refundCase.Id)));
        Is(!(await Run(cases, "accept", new RideFinancialCaseModel(Id: refundCase.Id))).Success, key + ": refund cannot post twice");
        Is(await db.PaymentTransactions.Where(x => x.RideId == matrix.Id && x.Status == PaymentStatus.Refunded).SumAsync(x => x.Amount) == 900, key + ": rider receives 900 and fees remain 100");
        Is(await db.Wallets.Where(x => x.UserId == driver.Id).Select(x => x.Balance).SingleAsync() == driverBefore, key + ": original electronic earning is reversed exactly once");
        var currentNetDebit = await db.JournalLines.Where(x => x.RideId == matrix.Id && x.LedgerAccount.Purpose == LedgerAccountPurpose.DriverCurrentAccount).SumAsync(x => x.Debit - x.Credit);
        Is(currentNetDebit == (originalCash ? returnCash ? 100m : 1000m : returnCash ? -900m : 0m), key + ": correct driver current account for payment/refund combination");
        var riderAfter = await db.Wallets.Where(x => x.UserId == customer.Id).Select(x => x.Balance).SingleAsync();
        Is(riderAfter - riderBefore == (originalCash ? 0 : -1000) + (returnCash ? 0 : 900), key + ": no duplicate credit to rider wallet");
        Is(await db.JournalLines.Where(x => x.RideId == matrix.Id && x.LedgerAccount.Purpose == LedgerAccountPurpose.ServiceFee).SumAsync(x => x.Credit - x.Debit) == 100, key + ": service fee is never refunded");
    }
    var journals = await db.JournalEntries.Include(x => x.Lines).ToListAsync();
    Is(journals.All(x => x.TotalDebit == x.TotalCredit && x.Lines.Sum(l => l.Debit) == x.Lines.Sum(l => l.Credit)), "every economic event has a balanced journal");
    Console.WriteLine("PASS: operational correction scenarios on isolated LocalDB; no real provider calls.");
}
finally
{
    await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
    if (File.Exists(settingsFile)) File.Delete(settingsFile);
}
static async Task<ApiResult> Run<T>(IModelService service, string operation, T model) => await service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), default);
static void Ok(ApiResult result) { if (!result.Success) throw new Exception(result.Code + ": " + result.Message); }
static void Is(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS: " + label); }
sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("Unexpected network call in isolated financial test"); }
