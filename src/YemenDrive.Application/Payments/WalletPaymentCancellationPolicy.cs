using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Payments;

public static class WalletPaymentCancellationPolicy
{
    public static bool CanCancel(PaymentWalletOperation operation)
    {
        if (!operation.IsOpen || operation.PaymentTransactionId is not null ||
            operation.ProviderSucceededAtUtc is not null || operation.PaidAtUtc is not null) return false;
        if (operation.Status is "AwaitingConfirmation" or "AwaitingCode") return true;
        // Floosak Request only prepares offline_cashout customer data. No OTP or
        // debit has been sent at this stage. Do not generalize this to other
        // providers, later stages, receipt mismatches, or calls still in flight.
        return string.IsNullOrWhiteSpace(operation.ProviderReference) && operation.ProviderCode == "Floosak" && operation.Stage == "Request" &&
            operation.Status == "ReviewRequired" &&
            operation.ErrorCode is "provider_failed" or "outcome_unknown";
    }

    // Caller holds the ride payment lock in a transaction. The same lock guards
    // Complete/Confirm, so cancellation cannot release a dispatched debit.
    public static async Task CancelUnsubmittedAsync(YemenDriveDbContext db, int rideId, CancellationToken token)
    {
        var operations = await db.PaymentWalletOperations.Where(x => x.RideId == rideId && x.IsOpen).ToListAsync(token);
        foreach (var operation in operations.Where(CanCancel))
        {
            operation.Status = "Cancelled";
            operation.Stage = "Cancel";
            operation.NextAction = "None";
            operation.IsOpen = false;
            operation.ErrorCode = "cancelled";
            operation.ProviderMessageAr = null;
            operation.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(token);
    }
}
