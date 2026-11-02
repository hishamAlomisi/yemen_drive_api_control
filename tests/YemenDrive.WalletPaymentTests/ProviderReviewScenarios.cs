using Microsoft.EntityFrameworkCore;
using YemenDrive.Api.WalletPayments;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

internal static class ProviderReviewScenarios
{
    public static async Task RunAsync(DbContextOptions<YemenDriveDbContext> options, IWalletPaymentGateway gateway, int customer, int driver, int kind, int catalog, int method)
    {
        await using var db = new YemenDriveDbContext(options);
        var admin = new User { PhoneNumber = "777123459", DisplayName = "Review admin", Role = UserRole.Admin };
        db.Users.Add(admin); await db.SaveChangesAsync();
        db.AdministratorPermissions.Add(new() { UserId = admin.Id, Permission = "finance.write" }); await db.SaveChangesAsync();
        var service = new WalletPaymentService(db, new Actor(admin.Id), gateway, new ExternalWalletPaymentSettlement(db, new(db, new(db))));
        var ride = new Ride { CustomerId = customer, DriverId = driver, ServiceKindId = kind, ServiceCatalogItemId = catalog, Status = RideStatus.Completed,
            TotalAmount = 1000, CustomerPrice = 900, ServiceFee = 100, DriverShare = 850, DriverCommissionAmount = 50, ServiceEndedAtUtc = DateTime.UtcNow };
        db.Rides.Add(ride); await db.SaveChangesAsync();
        var denied = new PaymentWalletOperation { RideId = ride.Id, CustomerId = customer, PaymentMethodId = method, ProviderCode = "Floosak", Amount = 1000,
            RequestId = Guid.NewGuid().ToString("N"), Status = "ReviewRequired", IsOpen = true, Mobile = "777123456", Currency = "YER" };
        db.PaymentWalletOperations.Add(denied); await db.SaveChangesAsync();
        await Reject(() => service.ReviewOutcomeAsync(denied.Id, "Failed", 999, "", "provider rejection statement", "wrong amount", default));
        var rejected = await service.ReviewOutcomeAsync(denied.Id, "Failed", 1000, "", "provider rejection statement", "verified refusal, no debit", default);
        Assert(rejected.Status == "Failed" && !denied.IsOpen && !await db.PaymentTransactions.AnyAsync(x => x.RideId == ride.Id), "definitive failed outcome releases original attempt with no accounting receipt");
        var late = new PaymentWalletOperation { RideId = ride.Id, CustomerId = customer, PaymentMethodId = method, ProviderCode = "Floosak", Amount = 1000,
            RequestId = Guid.NewGuid().ToString("N"), Status = "ReviewRequired", IsOpen = true, Mobile = "777123456", Currency = "YER" };
        ride.Status = RideStatus.Cancelled; ride.FinancialAmountDue = 100; ride.FinancialDriverEntitlement = 0;
        db.PaymentWalletOperations.Add(late);
        var cancellation = new RideCancellationRequest { RideId = ride.Id, CustomerId = customer, DriverId = driver, Reason = "prestart cancellation", RideStatusAtRequest = RideStatus.DriverArrived,
            Status = RideCancellationStatus.AdminApproved, AdminDecidedAtUtc = DateTime.UtcNow, TravelledFareSnapshot = 0 };
        db.RideCancellationRequests.Add(cancellation); await db.SaveChangesAsync();
        await Reject(() => service.ReviewOutcomeAsync(late.Id, "Succeeded", 1000, "", "statement", "missing reference", default));
        var result = await service.ReviewOutcomeAsync(late.Id, "Succeeded", 1000, "verified-late-reference", "original-provider-statement", "verified original debit", default);
        Assert(result.Status == "Succeeded" && ride.Status == RideStatus.Cancelled, "late verified success settles original debit without reopening transport");
        await db.Entry(cancellation).ReloadAsync();
        Assert(cancellation.Status == RideCancellationStatus.FinancialReviewPending && !await db.RideFinancialCases.AnyAsync(x => x.RideId == ride.Id && x.Kind == "FareDecrease"), "late cancellation receipt reopens one financial review without duplicate refund cases");
        var count = await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid);
        await service.ReviewOutcomeAsync(late.Id, "Succeeded", 1000, "verified-late-reference", "original-provider-statement", "repeated verified result", default);
        Assert(await db.PaymentTransactions.CountAsync(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid) == count, "review replay records no duplicate receipt");
        await Reject(() => service.ReviewOutcomeAsync(late.Id, "Failed", 1000, "verified-late-reference", "statement", "contradictory outcome", default));
        await Reject(() => new WalletPaymentService(db, new Actor(customer), gateway, new(db, new(db, new(db)))).ReviewOutcomeAsync(late.Id, "Succeeded", 1000, "verified-late-reference", "statement", "unauthorized", default));
        Console.WriteLine("PASS: provider review permission, amount/reference evidence, verified refusal, late success, immutable replay and exactly one cancellation review.");
    }
    private static void Assert(bool condition, string label) { if (!condition) throw new Exception(label); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (ServiceException) { return; } throw new Exception("Expected review validation rejection"); }
    private sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
}
