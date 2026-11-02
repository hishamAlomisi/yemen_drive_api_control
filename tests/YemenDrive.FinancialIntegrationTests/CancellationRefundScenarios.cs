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
using CancelService = YemenDrive.Application.Rides.RideCancellationRequest;

internal static class CancellationRefundScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int admin, int kind, int catalog)
    {
        var customer = new User { PhoneNumber = "+967700126550", DisplayName = "Refund rider", Role = UserRole.Customer };
        var driver = new User { PhoneNumber = "+967700126551", DisplayName = "Refund driver", Role = UserRole.Driver };
        db.AddRange(customer, driver); await db.SaveChangesAsync();
        var customerWallet = new Wallet { UserId = customer.Id, Currency = "YER", Balance = 100000 };
        var driverWallet = new Wallet { UserId = driver.Id, Currency = "YER", Balance = 0 };
        db.AddRange(customerWallet, driverWallet); await db.SaveChangesAsync();
        var provisioning = new FinancialAccountProvisioningService(db);
        await provisioning.ProvisionUserAsync(customer, CancellationToken.None);
        await provisioning.ProvisionUserAsync(driver, CancellationToken.None);
        var methods = new Dictionary<string, YemenDrive.Database.Entities.PaymentMethod>();
        foreach (var provider in new[] { "Jaib", "Floosak" })
        {
            var method = new YemenDrive.Database.Entities.PaymentMethod { Code = "refund-" + provider, NameAr = provider,
                ProviderCode = provider, IsActive = true, IsAvailableForRidePayment = true, Kind = PaymentMethodKind.ExternalWallet };
            db.Add(method); await db.SaveChangesAsync();
            await provisioning.ProvisionPaymentMethodAsync(method, CancellationToken.None);
            methods.Add(provider, method);
        }
        await db.SaveChangesAsync();
        var accounting = new RideAccountingPostingService(db, new AccountingPostingService(db));
        var rider = new CancelService(db, config, new Actor(customer.Id), accounting);
        var chauffeur = new CancelService(db, config, new Actor(driver.Id), accounting);
        var manager = new CancelService(db, config, new Actor(admin), accounting);
        var settlement = new ExternalWalletPaymentSettlement(db, accounting);

        async Task<YemenDrive.Database.Entities.Ride> Trip(bool started = false, bool track = true)
        {
            var now = DateTime.UtcNow.AddSeconds(-2);
            var ride = new YemenDrive.Database.Entities.Ride { CustomerId = customer.Id, DriverId = driver.Id,
                ServiceKindId = kind, ServiceCatalogItemId = catalog, Status = started ? RideStatus.InProgress : RideStatus.DriverAssigned,
                CustomerPrice = 900, ServiceFee = 100, CancellationFee = 50, TotalAmount = 1000,
                DriverCommissionAmount = 90, DriverShare = 810, PlatformShare = 190,
                StartedAtUtc = started ? now.AddMinutes(-5) : null,
                HasRoutePricingSnapshot = track, RouteSegmentStartedAtUtc = now.AddMinutes(-5),
                RouteSegmentLatitude = 15, RouteSegmentLongitude = 44, RouteBaseFare = 0,
                RoutePerKilometer = 100, RoutePerMinute = 0, RouteQuotedDistanceMeters = 9000,
                RouteQuotedDurationSeconds = 900, RouteOriginalFare = 900 };
            db.Add(ride); await db.SaveChangesAsync();
            if (started && track)
                for (var i = 0; i <= 5; i++) db.LocationUpdates.Add(new LocationUpdate { RideId = ride.Id, ActorId = driver.Id,
                    Latitude = 15 + i * .001, Longitude = 44, Speed = 2, Accuracy = 5, ObservedAtUtc = now.AddMinutes(-5 + i) });
            await db.SaveChangesAsync();
            return ride;
        }
        async Task<PaymentWalletOperation> Pay(YemenDrive.Database.Entities.Ride ride, string provider, bool dispatchOnly = false)
        {
            var op = new PaymentWalletOperation { RideId = ride.Id, CustomerId = customer.Id, PaymentMethodId = methods[provider].Id,
                ProviderCode = provider, RequestId = Guid.NewGuid().ToString("N"), Amount = 1000, Currency = "YER",
                Status = dispatchOnly ? "Processing" : "ProviderSucceeded", Stage = "Complete", IsOpen = true,
                ProviderSucceededAtUtc = dispatchOnly ? null : DateTime.UtcNow, ProviderReference = Guid.NewGuid().ToString("N") };
            db.Add(op); await db.SaveChangesAsync();
            if (!dispatchOnly) await settlement.SettleAsync(op, CancellationToken.None);
            return op;
        }
        async Task<YemenDrive.Database.Entities.RideCancellationRequest> Request(YemenDrive.Database.Entities.Ride ride)
        {
            Ok(await Call(rider, "add", new(RideId: ride.Id, Reason: "طرأ ظرف عاجل", ReviewedTravelledFare: 0, SafeStopConfirmed: ride.StartedAtUtc != null)), "customer cancellation");
            return await db.RideCancellationRequests.SingleAsync(x => x.RideId == ride.Id);
        }
        async Task Approve(YemenDrive.Database.Entities.RideCancellationRequest request, decimal? reviewed = null)
        {
            Ok(await Call(chauffeur, "accept", new(Id: request.Id)), "driver cancellation acceptance");
            Check(request.Status == RideCancellationStatus.AdminReviewPending, "paid case passes through admin review");
            Ok(await Call(manager, "adminApprove", new(Id: request.Id, ReviewedTravelledFare: reviewed, Note: "verified original policy and responsibility")), "admin approval");
            Ok(await Call(rider, "accept", new(Id: request.Id, RefundChoiceRevision: request.RefundChoiceRevision)), "rider chooses app wallet refund");
        }
        async Task Balanced(int rideId)
        {
            var entries = await db.JournalEntries.Include(x => x.Lines).Where(x => x.Lines.Any(l => l.RideId == rideId)).ToListAsync();
            Check(entries.Count == 2 && entries.All(x => x.Lines.Sum(l => l.Debit) == x.Lines.Sum(l => l.Credit)), "payment/refund journals balance");
            var walletParty = await db.FinancialParties.Where(p => p.Type == FinancialPartyType.Wallet && p.EntityId == driverWallet.Id).Select(p => p.Id).SingleAsync();
            var walletAccount = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == walletParty);
            var liability = entries.SelectMany(x => x.Lines).Where(x => x.LedgerAccountId == walletAccount.Id).Sum(x => x.Credit - x.Debit);
            var walletMovement = await db.WalletTransactions.Where(x => x.RideId == rideId && x.WalletId == driverWallet.Id)
                .SumAsync(x => x.Type == WalletTransactionType.Credit ? x.Amount : -x.Amount);
            Check(liability == walletMovement, "driver journal liability reconciles to actual wallet movements");
        }

        var exactCancellation = await Trip();
        exactCancellation.CancellationFee = 0; exactCancellation.DriverCommissionAmount = 0; exactCancellation.DriverShare = 900; exactCancellation.PlatformShare = 100;
        await db.SaveChangesAsync();
        var exactDriverBefore = driverWallet.Balance;
        var exactRiderBefore = customerWallet.Balance;
        await Pay(exactCancellation, "Floosak");
        Check(driverWallet.Balance == exactDriverBefore + 900, "1000 electronic payment credits driver net 900");
        var exactRequest = await Request(exactCancellation);
        await Approve(exactRequest);
        Check(customerWallet.Balance == exactRiderBefore + 900 && driverWallet.Balance == exactDriverBefore, "900 cancellation refund retains 100 fee and fully reverses driver 900");
        await Balanced(exactCancellation.Id);
        foreach (var provider in methods.Keys)
        {
            var ride = await Trip(); var beforeCustomer = customerWallet.Balance; var beforeDriver = driverWallet.Balance;
            await Pay(ride, provider);
            var preview = await RideCancellationQuote.GetAsync(db, ride, CancellationToken.None);
            Check(preview.RefundAmount == 850 && !preview.RideStarted && preview.TotalDeductions == 150, "prestart quote retains exact service/cancel fees");
            var request = await Request(ride);
            Check(customerWallet.Balance == beforeCustomer && request.RequestedRefundAmount == 850, "request cannot refund early");
            Ok(await Call(chauffeur, "accept", new(Id: request.Id)), "driver acceptance");
            Check(customerWallet.Balance == beforeCustomer && request.Status == RideCancellationStatus.AdminReviewPending, "driver cannot release funds");
            Ok(await Call(manager, "adminApprove", new(Id: request.Id, Note: "verified original policy and responsibility")), "admin establishes refund");
            Check(customerWallet.Balance == beforeCustomer, "external refund waits for informed destination consent");
            Ok(await Call(rider, "accept", new(Id: request.Id, RefundChoiceRevision: request.RefundChoiceRevision)), "rider chooses app wallet refund");
            Check(customerWallet.Balance == beforeCustomer + 850 && driverWallet.Balance == beforeDriver, "external payment refunds internal wallet and reverses full unearned driver share");
            Check(!(await Call(manager, "adminApprove", new(Id: request.Id, Note: "duplicate"))).Success, "closed administrative approval is rejected");
            Check(await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Refunded) == 1 &&
                await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid && x.Amount == 1000) == 1, "original payment immutable and refund once");
            Check(await db.Notifications.AnyAsync(x => x.UserId == customer.Id && x.Title == "اكتمل الإلغاء والاسترداد" &&
                x.Body.Contains("850") && x.DataJson!.Contains($"\"rideId\":{ride.Id},")), "customer receives the confirmed refund amount and a link to its own ride");
            await Balanced(ride.Id);
        }
        var travelling = await Trip(true); await Pay(travelling, "Jaib");
        var movingRequest = await Request(travelling);
        var movingQuote = await RideCancellationQuote.GetAsync(db, travelling, CancellationToken.None, movingRequest);
        Check(movingQuote.TravelledFare == 56 && movingQuote.RefundAmount == 794 && movingQuote.TravelledDistanceMeters is > 555 and < 557,
            "trusted GPS gives actual traveled fare and request cannot spoof reviewed fare");
        db.LocationUpdates.Add(new LocationUpdate { RideId = travelling.Id, ActorId = driver.Id, Latitude = 15.05,
            Longitude = 44, Speed = 2, ObservedAtUtc = DateTime.UtcNow.AddMinutes(2) }); await db.SaveChangesAsync();
        Check((await RideCancellationQuote.GetAsync(db, travelling, CancellationToken.None, movingRequest)).RefundAmount == 794, "later movement cannot alter frozen quote");
        var beforeMovingRefund = customerWallet.Balance;
        await Approve(movingRequest);
        Check(customerWallet.Balance == beforeMovingRefund + 794 && driverWallet.Balance == 50.40m, "driver retains only proportional traveled-fare entitlement");
        await Balanced(travelling.Id);

        var missing = await Trip(true, false); await Pay(missing, "Floosak");
        var missingRequest = await Request(missing);
        Check(missingRequest.RequestedRefundAmount is null, "missing route permits request without invented refund");
        Ok(await Call(chauffeur, "accept", new(Id: missingRequest.Id, Note: "verified evidence and original policy")), "missing-track driver acceptance");
        Check((await Call(manager, "adminApprove", new(Id: missingRequest.Id, Note: "verified evidence and original policy"))).Code == "cancellation_refund_review_required", "unknown fare cannot be auto-approved");
        db.ChangeTracker.Clear();
        customerWallet = await db.Wallets.SingleAsync(x => x.UserId == customer.Id);
        driverWallet = await db.Wallets.SingleAsync(x => x.UserId == driver.Id);
        driverWallet.Balance = 20; await db.SaveChangesAsync();
        var beforeMissingRefund = customerWallet.Balance;
        Ok(await Call(manager, "adminApprove", new(Id: missingRequest.Id, ReviewedTravelledFare: 100, Note: "verified delivered fare and original policy")), "admin reviewed fare");
        Ok(await Call(rider, "accept", new(Id: missingRequest.Id, RefundChoiceRevision: 0)), "rider chooses refund destination after review");
        Check(customerWallet.Balance == beforeMissingRefund + 750 && driverWallet.Balance == 0, "driver shortfall does not block customer refund");
        Check(await db.DriverSettlements.AnyAsync(x => x.DriverId == driver.Id && x.Status == PaymentStatus.Pending &&
            x.PaymentReference!.EndsWith(":driver-current") && x.NetPayable == -700), "exact unrecovered share becomes separate driver debt");
        await Balanced(missing.Id);

        var late = await Trip(); var inFlight = await Pay(late, "Jaib", true);
        var lateRequest = await Request(late);
        Ok(await Call(chauffeur, "accept", new(Id: lateRequest.Id, Note: "verified original provider result")), "inflight cancellation reaches admin");
        Check((await Call(manager, "adminApprove", new(Id: lateRequest.Id, Note: "verified original provider result"))).Code == "wallet_payment_in_progress", "uncertain debit prevents premature financial settlement");
        db.ChangeTracker.Clear();
        inFlight = await db.PaymentWalletOperations.SingleAsync(x => x.Id == inFlight.Id);
        inFlight.Status = "ProviderSucceeded"; inFlight.ProviderSucceededAtUtc = DateTime.UtcNow; await db.SaveChangesAsync();
        await settlement.SettleAsync(inFlight, CancellationToken.None);
        Ok(await Call(manager, "adminApprove", new(Id: lateRequest.Id, Note: "verified original provider result")), "late confirmed debit can settle and refund cancellation");
        Ok(await Call(rider, "accept", new(Id: lateRequest.Id, RefundChoiceRevision: 0)), "rider chooses destination for late receipt refund");
        Check(await db.PaymentTransactions.CountAsync(x => x.RideId == late.Id && x.Status == PaymentStatus.Paid) == 1 &&
            await db.PaymentTransactions.CountAsync(x => x.RideId == late.Id && x.Status == PaymentStatus.Refunded && x.Amount == 850) == 1,
            "late debit and refund each happen once");
        var stranger = new CancelService(db, config, new Actor(driver.Id + 100000), accounting);
        Check((await Call(stranger, "get", new(RideId: late.Id, PreviewOnly: true))).Code == "ride_access_denied", "refund preview enforces ownership");
        Console.WriteLine("PASS: paid cancellation quotes, GPS freeze, review order, accounting, shortfall and late-confirmed wallet debit.");
    }
    static Task<ApiResult> Call(CancelService service, string operation, RideCancellationRequestModel model) =>
        service.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), CancellationToken.None);
    static void Ok(ApiResult result, string action) => Check(result.Success, action + ": " + result.Code);
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; public string? PhoneNumber => null; }
}
