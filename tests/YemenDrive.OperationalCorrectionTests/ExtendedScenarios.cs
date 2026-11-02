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
using Ride = YemenDrive.Database.Entities.Ride;
using LedgerAccount = YemenDrive.Database.Entities.LedgerAccount;
using RideCancellationRequest = YemenDrive.Application.Rides.RideCancellationRequest;

internal static class ExtendedScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int customer, int driver, int admin, int kind, int service)
    {
        var rider = new Actor(customer); var chauffeur = new Actor(driver); var manager = new Actor(admin);
        var accounting = new RideAccountingPostingService(db, new(db));
        var trip = new Ride { CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = service,
            Status = RideStatus.DriverAssigned, PickupLatitude = 15, PickupLongitude = 44, TotalAmount = 1000, CustomerPrice = 900, ServiceFee = 100, DriverShare = 850 };
        db.Rides.Add(trip); await db.SaveChangesAsync();
        var live = await db.DriverLiveLocations.SingleOrDefaultAsync(x => x.DriverId == driver);
        if (live != null) { live.IsOnline = false; await db.SaveChangesAsync(); }
        await Fails(() => RidePickupService.VerifyArrivalAsync(db, trip, default), "missing GPS cannot fabricate arrival");
        if (live == null) { live = new() { DriverId = driver }; db.DriverLiveLocations.Add(live); }
        live.Latitude = 15; live.Longitude = 44; live.IsOnline = true; live.ObservedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync();
        await Fails(() => RidePickupService.VerifyArrivalAsync(db, trip, default), "missing approved radius requires rider-confirmed pickup");
        trip.ArrivalToleranceMeters = 50; await db.SaveChangesAsync();
        await RidePickupService.VerifyArrivalAsync(db, trip, default);
        trip.Status = RideStatus.DriverArrived; trip.DriverArrivedAtUtc = DateTime.UtcNow.AddMinutes(-2); trip.PickupGraceMinutes = 1; await db.SaveChangesAsync();
        var pickup = new RidePickupService(db, config, chauffeur);
        var initial = JsonSerializer.SerializeToElement((await Run(pickup, "get", new RidePickupModel(trip.Id))).Data);
        Check(!initial.GetProperty("noShowEligible").GetBoolean(), "elapsed grace alone cannot prove no-show");
        Ok(await Run(pickup, "contact", new RidePickupModel(trip.Id, "called without answer")));
        var contacted = JsonSerializer.SerializeToElement((await Run(pickup, "get", new RidePickupModel(trip.Id))).Data);
        Check(contacted.GetProperty("noShowEligible").GetBoolean(), "verified arrival, grace and actual contact permit reviewable no-show cancellation");
        trip.PassengerBoardedAtUtc = DateTime.UtcNow; trip.Status = RideStatus.InProgress; trip.StartedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync();
        var cancel = new RideCancellationRequest(db, config, rider, accounting);
        Ok(await Run(cancel, "add", new RideCancellationRequestModel(RideId: trip.Id, Reason: "safe stop, unable to pay", SafeStopConfirmed: true)));
        await RideFinancialState.SynchronizeAsync(db, trip, customer, default);
        var finance = await RideFinancialState.ReadAsync(db, trip, default);
        Check(finance.AmountUnderReview && !finance.PaymentCompleted, "unreviewed cancellation amount is not confirmed debt");
        Check((await db.RideFinancialCases.SingleAsync(x => x.RideId == trip.Id)).AccruedReceivable == 0, "unreviewed original fare creates no final receivable journal");
        var cancellation = await db.RideCancellationRequests.SingleAsync(x => x.RideId == trip.Id);
        cancellation.ReviewDueAtUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        await OperationalReviewCoordinator.ReconcileAsync(db, default);
        cancellation = await db.RideCancellationRequests.SingleAsync(x => x.Id == cancellation.Id);
        Check(cancellation.Status == RideCancellationStatus.FinancialReviewPending && cancellation.CustomerCashReceiptConfirmed != true && cancellation.AssignedAdminId == admin,
            "expired response escalates to authorized owner without fabricating cash receipt");

        var cashTrip = new Ride { CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = service,
            Status = RideStatus.InProgress, StartedAtUtc = DateTime.UtcNow, TotalAmount = 1000, CustomerPrice = 900, ServiceFee = 100, DriverShare = 850, DriverCommissionAmount = 50 };
        db.Rides.Add(cashTrip); await db.SaveChangesAsync();
        Ok(await Run(new YemenDrive.Application.DriverPayments.DriverCashPayment(db, config, chauffeur, accounting), "add",
            new YemenDrive.Application.DriverPayments.DriverCashPaymentModel(cashTrip.Id, 1100, "YER", IdempotencyKey: "verified-payout-cash")));
        var change = await db.RideFinancialCases.SingleAsync(x => x.RideId == cashTrip.Id && x.Kind == "CashChange");
        var bank = new FinancialParty { Name = "Test payout source", Type = FinancialPartyType.Bank };
        db.FinancialParties.Add(bank); await db.SaveChangesAsync();
        var source = new LedgerAccount { Code = "TEST-PAYOUT", Name = "Test bank", Type = LedgerAccountType.Asset, FinancialPartyId = bank.Id, IsActive = true, IsPosting = true, Currency = "YER" };
        var equity = new LedgerAccount { Code = "TEST-EQUITY", Name = "Test funding", Type = LedgerAccountType.Equity, IsActive = true, IsPosting = true, Currency = "YER" };
        db.LedgerAccounts.AddRange(source, equity); await db.SaveChangesAsync();
        await Fails(() => VerifiedRefundPayout.ValidateAsync(db, admin, source.Id, 100, "independent evidence", default), "unfunded source cannot record actual payout");
        await new AccountingPostingService(db).PostAsync(new(JournalEntryType.ManualAdjustment, "test-source-funding", "isolated source funding", "YER", [new(source.Id, Debit: 500), new(equity.Id, Credit: 500)], SourceType: "TestFunding", SourceId: "test-source-funding", CreatedByUserId: admin), default);
        await db.SaveChangesAsync();
        await Fails(() => VerifiedRefundPayout.ValidateAsync(db, admin, source.Id, 100, null, default), "payout requires actual transfer and customer receipt evidence");
        await Fails(() => VerifiedRefundPayout.ValidateAsync(db, customer, source.Id, 100, "evidence", default), "customer cannot authorize payout source");
        var balance = await db.Wallets.Where(x => x.UserId == customer).Select(x => x.Balance).SingleAsync();
        var adminCases = new RideFinancialCaseService(db, config, manager);
        Ok(await Run(adminCases, "update", new RideFinancialCaseModel(Id: change.Id, Resolution: "VerifiedPayout", VerifiedAmount: 100, FundingLedgerAccountId: source.Id, EvidenceReference: "independent-receipt-and-bank-transfer", Note: "verified transfer")));
        Check(await db.Wallets.Where(x => x.UserId == customer).Select(x => x.Balance).SingleAsync() == balance, "verified alternative payout does not also credit app wallet");
        Check(await db.JournalLines.Where(x => x.LedgerAccountId == source.Id).SumAsync(x => x.Debit - x.Credit) == 400, "actual refund is credited from the verified funding source");
        Check(!(await Run(adminCases, "update", new RideFinancialCaseModel(Id: change.Id, Resolution: "VerifiedPayout", VerifiedAmount: 100, FundingLedgerAccountId: source.Id, EvidenceReference: "duplicate", Note: "duplicate"))).Success,
            "closed payout cannot post twice");

        var partial = new Ride { CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = service, Status = RideStatus.InProgress,
            TotalAmount = 1000, CustomerPrice = 900, ServiceFee = 100, DriverShare = 850, DriverCommissionAmount = 50 };
        db.Rides.Add(partial); await db.SaveChangesAsync();
        Ok(await Run(new YemenDrive.Application.DriverPayments.DriverCashPayment(db, config, chauffeur, accounting), "add", new YemenDrive.Application.DriverPayments.DriverCashPaymentModel(partial.Id, 400, "YER", IdempotencyKey: "partial-cancel-fee")));
        var originalReceipt = await db.PaymentTransactions.SingleAsync(x => x.RideId == partial.Id && x.Status == PaymentStatus.Paid);
        Check(originalReceipt.ServiceFeeAmount == 40, "original partial receipt retains proportional fee allocation");
        partial.Status = RideStatus.DriverArrived; await db.SaveChangesAsync();
        Ok(await Run(cancel, "add", new RideCancellationRequestModel(RideId: partial.Id, Reason: "changed plans before boarding")));
        var refund = await db.RideCancellationRequests.SingleAsync(x => x.RideId == partial.Id);
        var driverCancel = new RideCancellationRequest(db, config, chauffeur, accounting);
        Ok(await Run(driverCancel, "accept", new RideCancellationRequestModel(Id: refund.Id)));
        Ok(await Run(new RideCancellationRequest(db, config, manager, accounting), "adminApprove", new RideCancellationRequestModel(Id: refund.Id, Note: "original approved service fee, no distance travelled")));
        Ok(await Run(driverCancel, "update", new RideCancellationRequestModel(Id: refund.Id, RequestedRefundMethod: CashCancellationRefundMethod.CreditCustomerWallet, RefundChoiceRevision: 0)));
        Ok(await Run(cancel, "accept", new RideCancellationRequestModel(Id: refund.Id, RefundChoiceRevision: 1)));
        Check(await db.PaymentTransactions.Where(x => x.RideId == partial.Id && x.Status == PaymentStatus.Refunded).SumAsync(x => x.Amount) == 300, "partial cancellation returns actual receipt minus approved fee");
        var retainedFee = await db.JournalLines.Where(x => x.RideId == partial.Id && x.LedgerAccount.Purpose == LedgerAccountPurpose.ServiceFee && x.JournalEntry.IsPosted).SumAsync(x => x.Credit - x.Debit);
        Check(retainedFee == 100 && originalReceipt.ServiceFeeAmount == 40, "adjustment reconciles retained policy fee without rewriting original receipt allocation");

        var mixed = new Ride { CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = service, Status = RideStatus.Completed, TotalAmount = 1000, CollectedDriverEntitlement = 850 };
        db.Rides.Add(mixed); await db.SaveChangesAsync();
        var cashReceipt = new PaymentTransaction { UserId = customer, RideId = mixed.Id, Amount = 400, Currency = "YER", Provider = "Cash", Status = PaymentStatus.Paid, DriverNetAmount = 340, ProviderReference = "mixed-cash" };
        db.PaymentTransactions.Add(cashReceipt); await db.SaveChangesAsync();
        var walletReceipt = new PaymentTransaction { UserId = customer, RideId = mixed.Id, Amount = 600, Currency = "YER", Provider = "YemenDriveWallet", Status = PaymentStatus.Paid, DriverNetAmount = 510, IsSupplemental = true, ProviderReference = "mixed-wallet" };
        db.PaymentTransactions.Add(walletReceipt); await db.SaveChangesAsync();
        Check(await RideFinancialState.AllocateDriverReversalAsync(db, mixed, 600, default) == 510, "mixed receipt reversal debits only electronic share once"); await db.SaveChangesAsync();
        Check(await RideFinancialState.AllocateDriverReversalAsync(db, mixed, 100, default) == 0 && cashReceipt.DriverEntitlementReversed == 190,
            "subsequent refund consumes remaining cash entitlement without a second electronic debit"); await db.SaveChangesAsync();
    }
    private static Task<ApiResult> Run<T>(IModelService service, string operation, T model) => service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), default);
    private static void Ok(ApiResult result) { if (!result.Success) throw new Exception(result.Code + ": " + result.Message); }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
    private static async Task Fails(Func<Task> action, string label) { try { await action(); } catch (ServiceException) { Check(true, label); return; } throw new Exception(label); }
}
