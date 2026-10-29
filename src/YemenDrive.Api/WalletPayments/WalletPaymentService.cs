using System.Data;
using System.Text;
using eqsWallets;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api.WalletPayments;

public sealed record WalletPaymentView(int Id, int RideId, int PaymentMethodId, string Status, string NextAction,
    string? CustomerName, string Mobile, decimal Amount, decimal? Fees, bool FeesKnown, string Currency,
    string FeeCurrency, string Recipient, string? ProviderReference, string Message, DateTime? PaidAtUtc,
    string ProviderCode, string ProviderName, bool CustomerIsProviderVerified);

public sealed class WalletPaymentService(YemenDriveDbContext db, ICurrentUserContext currentUser,
    IWalletPaymentGateway gateway, ExternalWalletPaymentSettlement settlement,
    IRideRealtimePublisher? realtime = null)
{
    public async Task<WalletPaymentView?> GetAsync(int rideId, CancellationToken token)
    {
        await RequireCustomerAsync(rideId, token);
        var operation = await db.PaymentWalletOperations.Where(x => x.RideId == rideId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
        if (operation is null) return null;
        await RecoverAsync(operation, token);
        return View(operation);
    }

    public async Task<WalletPaymentView> StartAsync(int rideId, int paymentMethodId, CancellationToken token)
    {
        var customerId = currentUser.RequireUserId();
        PaymentWalletOperation operation;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
        {
            await RidePaymentGuard.LockAsync(db, rideId, token);
            var ride = await RequireCustomerAsync(rideId, token);
            var existing = await db.PaymentWalletOperations.SingleOrDefaultAsync(x => x.RideId == rideId && x.IsOpen, token);
            if (existing is not null)
            {
                await tx.CommitAsync(token);
                await RecoverAsync(existing, token);
                return View(existing);
            }
            await RidePaymentGuard.EnsureAvailableAsync(db, rideId, token);
            EnsureRideReady(ride);
            if (await db.RideCancellationRequests.AnyAsync(x => x.RideId == rideId &&
                (x.Status == RideCancellationStatus.DriverReviewPending || x.Status == RideCancellationStatus.AdminReviewPending ||
                 x.Status == RideCancellationStatus.CustomerReviewPending), token))
                throw new ServiceException("ride_cancellation_pending", "توجد مراجعة لإلغاء الرحلة. لا يمكن بدء الدفع قبل حسمها.");
            if (await db.CashCollectionApprovals.AnyAsync(x => x.RideId == rideId &&
                (x.Status == CashCollectionApprovalStatus.Pending || x.Status == CashCollectionApprovalStatus.Approved), token) ||
                await db.CashPaymentRequests.AnyAsync(x => x.RideId == rideId &&
                    (x.Status == CashPaymentRequestStatus.Pending || x.Status == CashPaymentRequestStatus.DriverConfirmed), token))
                throw new ServiceException("cash_payment_pending", "يوجد إجراء دفع نقدي جارٍ لهذه الرحلة. أكمله أو ارفضه أولاً.");
            var method = await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(x => x.Id == paymentMethodId, token);
            var account = await db.PaymentProviderAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentMethodId == paymentMethodId, token);
            if (method is null || !method.IsActive || !method.IsAvailableForRidePayment ||
                method.Kind != PaymentMethodKind.ExternalWallet || !WalletPaymentProviders.IsSupported(method.ProviderCode) ||
                account is null || !account.IsEnabled || account.ProviderCode != method.ProviderCode ||
                !gateway.IsReady(paymentMethodId, account.ConfigurationVersion))
                throw new ServiceException("wallet_payment_unavailable", "الدفع عبر المحفظة المختارة غير متاح حالياً. حاول لاحقاً.");
            RidePaymentPolicy.EnsureReady(ride, method.Code);
            await new RideWaitingCoordinator(db).EnsureSettledAsync(ride, token);
            await settlement.ValidateAsync(ride, paymentMethodId, token);
            var mobile = NormalizeMobile(ride.Customer.PhoneNumber, method.ProviderCode);
            var amount = AmountDue(ride);
            if (amount <= 0 || decimal.Round(amount, 2) != amount)
                throw new ServiceException("invalid_payment_amount", "مبلغ الرحلة غير صالح للدفع.");
            operation = new PaymentWalletOperation
            {
                RideId = ride.Id, CustomerId = customerId, PaymentMethodId = paymentMethodId, ProviderCode = method.ProviderCode,
                RequestId = "YD-" + Guid.NewGuid().ToString("N"), ConfigurationVersion = account.ConfigurationVersion,
                RuntimeInstanceId = gateway.InstanceId, Mobile = mobile, Amount = amount,
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.PaymentWalletOperations.Add(operation);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }
        await NotifyAsync(operation);
        return await RunProviderAsync(operation, ct => gateway.RequestAsync(operation.PaymentMethodId,
            operation.ConfigurationVersion, operation.ProviderCode, operation.RequestId, operation.Mobile, operation.Amount, ct));
    }

    public async Task<WalletPaymentView> ConfirmAsync(int rideId, int paymentId, bool approved, CancellationToken token)
    {
        var claimed = await ClaimAsync(rideId, paymentId, approved ? "Confirm" : "Cancel", token);
        var operation = claimed.Operation;
        if (!claimed.Claimed) return View(operation);
        if (!approved)
        {
            // No code has been submitted and no provider call is in flight. Abandoning this local
            // workflow cannot debit the customer; never contact an old session just to cancel it.
            SetTerminal(operation, "Cancelled", "cancelled");
            await db.SaveChangesAsync(CancellationToken.None);
            await NotifyAsync(operation);
            return View(operation);
        }
        return await RunProviderAsync(operation, ct => gateway.ConfirmAsync(operation.PaymentMethodId,
            operation.ConfigurationVersion, operation.ProviderCode, operation.ProviderOperationId!, true, ct));
    }

    public async Task<WalletPaymentView> CompleteAsync(int rideId, int paymentId, string? code, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 256)
            throw new ServiceException("verification_code_required", "أدخل الرمز أو رقم القسيمة المطلوب من المحفظة المختارة.");
        var claimed = await ClaimAsync(rideId, paymentId, "Complete", token);
        var operation = claimed.Operation;
        if (!claimed.Claimed) return View(operation);
        // This value exists only in the call stack, never in an entity, log or response.
        return await RunProviderAsync(operation, ct => gateway.CompleteAsync(operation.PaymentMethodId,
            operation.ConfigurationVersion, operation.ProviderCode, operation.ProviderOperationId!, code.Trim(), ct), code.Trim());
    }

    private async Task<(PaymentWalletOperation Operation, bool Claimed)> ClaimAsync(int rideId, int paymentId,
        string stage, CancellationToken token)
    {
        await RequireCustomerAsync(rideId, token);
        var operation = await db.PaymentWalletOperations.SingleOrDefaultAsync(x => x.Id == paymentId && x.RideId == rideId, token)
            ?? throw new ServiceException("wallet_payment_not_found", "عملية الدفع غير موجودة.");
        await RecoverAsync(operation, token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await RidePaymentGuard.LockAsync(db, rideId, token);
        await db.Entry(operation).ReloadAsync(token);
        var canClaim = stage == "Cancel"
            ? operation.Status is "AwaitingConfirmation" or "AwaitingCode"
            : stage == "Confirm" ? operation.Status == "AwaitingConfirmation" : operation.Status == "AwaitingCode";
        if (!canClaim) { await tx.CommitAsync(token); return (operation, false); }
        var ride = await db.Rides.SingleAsync(x => x.Id == rideId, token);
        if (stage != "Cancel")
        {
            var accountValid = await db.PaymentProviderAccounts.AsNoTracking().AnyAsync(x =>
                x.PaymentMethodId == operation.PaymentMethodId && x.IsEnabled && x.ProviderCode == operation.ProviderCode &&
                x.ConfigurationVersion == operation.ConfigurationVersion && x.PaymentMethod.IsActive &&
                x.PaymentMethod.IsAvailableForRidePayment && x.PaymentMethod.ProviderCode == operation.ProviderCode, token);
            if (!accountValid)
            {
                SetTerminal(operation, "Cancelled", "session_expired");
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                await NotifyAsync(operation);
                return (operation, false);
            }
            EnsureRideReady(ride);
            var paymentMethodCode = await db.PaymentMethods.AsNoTracking()
                .Where(x => x.Id == operation.PaymentMethodId).Select(x => x.Code).SingleAsync(token);
            RidePaymentPolicy.EnsureReady(ride, paymentMethodCode);
            if (AmountDue(ride) != operation.Amount ||
                await db.PaymentTransactions.AnyAsync(x => x.RideId == rideId && x.Status == PaymentStatus.Paid, token))
                throw new ServiceException("wallet_payment_ride_changed", "تغيرت حالة دفع الرحلة. حدّث بيانات الرحلة قبل المتابعة.");
            await settlement.ValidateAsync(ride, operation.PaymentMethodId, token);
        }
        operation.Stage = stage;
        operation.Status = "Processing";
        operation.NextAction = "None";
        operation.ProviderMessageAr = null;
        operation.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return (operation, true);
    }

    private async Task<WalletPaymentView> RunProviderAsync(PaymentWalletOperation operation,
        Func<CancellationToken, Task<WalletStepResult>> execute, string? submittedCode = null)
    {
        // A disconnected HTTP request must not interrupt outcome persistence after dispatch.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        WalletStepResult result;
        try { result = await execute(timeout.Token); }
        catch (WalletSessionUnavailableException)
        {
            SetTerminal(operation, "Cancelled", "session_expired");
            await db.SaveChangesAsync(CancellationToken.None);
            await NotifyAsync(operation);
            return View(operation);
        }
        catch (Exception)
        {
            SetTerminal(operation, "ReviewRequired", "outcome_unknown");
            await db.SaveChangesAsync(CancellationToken.None);
            await NotifyAsync(operation);
            return View(operation);
        }
        ApplyResult(operation, result, submittedCode);
        // Persist successful provider evidence before the separate, retryable accounting transaction.
        await db.SaveChangesAsync(CancellationToken.None);
        if (operation.Status == "ProviderSucceeded") await TrySettleAsync(operation);
        await NotifyAsync(operation);
        return View(operation);
    }

    private async Task RecoverAsync(PaymentWalletOperation operation, CancellationToken token)
    {
        if (operation.Status == "ProviderSucceeded") { await TrySettleAsync(operation); return; }
        if (!operation.IsOpen || operation.Status is "Succeeded" or "ReviewRequired") return;
        // Only one state transition can win against Confirm/Complete. Serialize on the same ride.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await RidePaymentGuard.LockAsync(db, operation.RideId, token);
        await db.Entry(operation).ReloadAsync(token);
        var changed = false;
        if (operation.Status == "Processing" && (operation.RuntimeInstanceId != gateway.InstanceId ||
            operation.UpdatedAtUtc < DateTime.UtcNow.AddMinutes(-3)))
        {
            SetTerminal(operation, operation.Stage == "Cancel" ? "Cancelled" : "ReviewRequired", "outcome_unknown");
            changed = true;
        }
        else if (operation.Status is "AwaitingConfirmation" or "AwaitingCode" &&
            (operation.RuntimeInstanceId != gateway.InstanceId || operation.ExpiresAtUtc <= DateTime.UtcNow ||
             !gateway.IsReady(operation.PaymentMethodId, operation.ConfigurationVersion)))
        {
            SetTerminal(operation, "Cancelled", "session_expired");
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        if (changed) await NotifyAsync(operation);
    }

    private async Task TrySettleAsync(PaymentWalletOperation operation)
    {
        try { await settlement.SettleAsync(operation, CancellationToken.None); }
        catch (Exception)
        {
            // The persisted ProviderSucceeded row remains recoverable on GET. Do not submit another debit.
            db.ChangeTracker.Clear();
            var stored = await db.PaymentWalletOperations.AsNoTracking().SingleAsync(x => x.Id == operation.Id, CancellationToken.None);
            db.Entry(operation).State = EntityState.Detached;
            operation.Status = stored.Status;
            operation.PaymentTransactionId = stored.PaymentTransactionId;
            operation.PaidAtUtc = stored.PaidAtUtc;
            operation.UpdatedAtUtc = stored.UpdatedAtUtc;
        }
    }

    private async Task<YemenDrive.Database.Entities.Ride> RequireCustomerAsync(int rideId, CancellationToken token)
    {
        var customerId = currentUser.RequireUserId();
        return await db.Rides.Include(x => x.Customer).SingleOrDefaultAsync(x => x.Id == rideId &&
            x.CustomerId == customerId && x.Customer.IsActive && x.Customer.Role == UserRole.Customer, token)
            ?? throw new ServiceException("ride_access_denied", "لا تملك صلاحية الوصول إلى هذه الرحلة.");
    }

    public static string NormalizeMobile(string value, string providerCode = "Floosak")
    {
        var digits = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            if (char.IsDigit(ch)) digits.Append((int)char.GetNumericValue(ch));
            else if (ch is not ('+' or '-' or '(' or ')' or ' '))
                throw new ServiceException("invalid_customer_mobile", "رقم الهاتف المسجل غير صالح للدفع عبر المحفظة.");
        }
        var phone = digits.ToString();
        if (phone.StartsWith("00967", StringComparison.Ordinal)) phone = phone[2..];
        if (phone.StartsWith("967", StringComparison.Ordinal) && phone.Length == 12) phone = phone[3..];
        if (phone.StartsWith('0') && phone.Length == 10) phone = phone[1..];
        if (phone.Length != 9 || phone[0] != '7')
            throw new ServiceException("invalid_customer_mobile", "يلزم رقم هاتف يمني صحيح في حسابك لإتمام الدفع عبر المحفظة.");
        return providerCode == "Jaib" ? phone : "967" + phone;
    }

    private static decimal AmountDue(YemenDrive.Database.Entities.Ride ride) =>
        ride.TotalAmount ?? (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) + ride.ServiceFee + ride.WaitingFee;

    private static void EnsureRideReady(YemenDrive.Database.Entities.Ride ride)
    {
        RidePaymentPolicy.EnsureReady(ride);
    }

    public static void ApplyResult(PaymentWalletOperation operation, WalletStepResult result, string? submittedCode = null)
    {
        ApplyResultCore(operation, result);
        operation.ProviderMessageAr = WalletProviderMessageFormatter.Format(result, operation.Status, submittedCode, operation.ProviderCode);
    }

    private static void ApplyResultCore(PaymentWalletOperation operation, WalletStepResult result)
    {
        operation.UpdatedAtUtc = DateTime.UtcNow;
        operation.NextAction = "None";
        var knownReceipt = result.Receipt;
        var knownReference = result.ProviderReference ?? knownReceipt?.Reference.ProviderTransactionId?.Value;
        if (knownReceipt is not null && knownReceipt.Amount.Amount == operation.Amount &&
            knownReceipt.Amount.Currency == CurrencyCode.YER && knownReceipt.Reference.ClientRequestId.Value == operation.RequestId &&
            !string.IsNullOrWhiteSpace(knownReference) && knownReference.Length <= 256 &&
            (operation.ProviderReference is null || operation.ProviderReference == knownReference))
            operation.ProviderReference = knownReference; // Useful to reconcile an uncertain result; never denotes success by itself.
        if (result.Status == OperationStatus.Succeeded)
        {
            var receipt = result.Receipt;
            var reference = result.ProviderReference ?? receipt?.Reference.ProviderTransactionId?.Value;
            if (receipt is null || receipt.Amount.Amount != operation.Amount || receipt.Amount.Currency != CurrencyCode.YER ||
                string.IsNullOrWhiteSpace(reference) || reference.Length > 256 ||
                (operation.ProviderReference is not null && operation.ProviderReference != reference) ||
                receipt.Reference.ClientRequestId.Value != operation.RequestId ||
                (receipt.Fees is { } paidFees && (paidFees.Currency != CurrencyCode.YER || paidFees.Amount < 0)) ||
                (!string.IsNullOrEmpty(result.RequestId) && result.RequestId != operation.RequestId))
            {
                SetTerminal(operation, "ReviewRequired", "provider_data_mismatch");
                return;
            }
            operation.ProviderReference = reference;
            operation.ProviderSucceededAtUtc = receipt.CompletedAt?.UtcDateTime ?? DateTime.UtcNow;
            operation.Status = "ProviderSucceeded";
            operation.ErrorCode = null;
            return;
        }
        if (result.Success && result.Status == OperationStatus.RequiresAction)
        {
            var action = result.NextAction.ToString();
            if (string.IsNullOrWhiteSpace(result.OperationId) || result.OperationId.Length > 256 ||
                (!string.IsNullOrEmpty(result.RequestId) && result.RequestId != operation.RequestId))
            { SetTerminal(operation, operation.Stage == "Complete" ? "ReviewRequired" : "Failed", "provider_data_mismatch"); return; }
            if (action == "Confirmation")
            {
                var customer = result.Customer;
                if (!ValidCustomer(operation, customer))
                { SetTerminal(operation, operation.Stage == "Complete" ? "ReviewRequired" : "Failed", "provider_data_mismatch"); return; }
                var verifiedCustomer = customer!;
                operation.CustomerName = verifiedCustomer.Name!.Trim();
                operation.FeesKnown = verifiedCustomer.FeesKnown && verifiedCustomer.Fees.HasValue;
                operation.Fees = operation.FeesKnown ? verifiedCustomer.Fees : null;
                operation.Status = "AwaitingConfirmation";
            }
            else if (action is "OneTimePassword" or "Pin" or "Voucher")
                operation.Status = "AwaitingCode";
            else
            { SetTerminal(operation, operation.Stage == "Complete" ? "ReviewRequired" : "Failed", "unsupported_workflow"); return; }
            operation.ProviderOperationId = result.OperationId;
            operation.NextAction = action;
            operation.ErrorCode = result.Success ? null : "verification_failed";
            operation.ExpiresAtUtc = result.ExpiresAt?.UtcDateTime ?? DateTime.UtcNow.AddMinutes(5);
            return;
        }
        var definitiveFailure = result.Status == OperationStatus.Failed && result.ErrorCode is
            WalletErrorCode.InsufficientFunds or WalletErrorCode.Rejected or WalletErrorCode.LimitExceeded or
            WalletErrorCode.InvalidRequest or WalletErrorCode.AuthenticationFailed or WalletErrorCode.WorkflowExpired or
            WalletErrorCode.WorkflowNotFound or WalletErrorCode.CanceledBeforeSend;
        var code = result.ErrorCode switch
        {
            WalletErrorCode.InsufficientFunds => "insufficient_funds",
            WalletErrorCode.Rejected => "provider_rejected",
            WalletErrorCode.LimitExceeded => "limit_exceeded",
            WalletErrorCode.WorkflowExpired or WalletErrorCode.WorkflowNotFound => "session_expired",
            WalletErrorCode.InvalidRequest => "verification_failed",
            _ => "provider_failed"
        };
        SetTerminal(operation, definitiveFailure ? "Failed" : "ReviewRequired", code);
    }

    private static bool ValidCustomer(PaymentWalletOperation operation, WalletCustomerData? customer)
    {
        if (customer is null || !customer.IsProviderVerified || string.IsNullOrWhiteSpace(customer.Name) ||
            customer.Name.Length > 250 || customer.Amount != operation.Amount ||
            !string.Equals(customer.Currency, "YER", StringComparison.OrdinalIgnoreCase) ||
            (customer.FeesKnown && customer.Fees.HasValue && (customer.Fees < 0 ||
                !string.Equals(customer.FeeCurrency, "YER", StringComparison.OrdinalIgnoreCase)))) return false;
        try { return NormalizeMobile(customer.Mobile ?? "", operation.ProviderCode) == operation.Mobile; }
        catch (ServiceException) { return false; }
    }

    private static void SetTerminal(PaymentWalletOperation operation, string status, string code)
    {
        operation.Status = status;
        operation.NextAction = "None";
        operation.IsOpen = status is not ("Failed" or "Cancelled");
        operation.ErrorCode = code;
        operation.ProviderMessageAr = null;
        operation.UpdatedAtUtc = DateTime.UtcNow;
    }

    public static WalletPaymentView View(PaymentWalletOperation operation) => new(operation.Id, operation.RideId,
        operation.PaymentMethodId, operation.Status == "ProviderSucceeded" ? "Processing" : operation.Status,
        operation.NextAction, operation.CustomerName, operation.Mobile, operation.Amount, operation.Fees,
        operation.FeesKnown, operation.Currency, operation.FeeCurrency, "يمن درايف", operation.ProviderReference,
        Message(operation), operation.PaidAtUtc, operation.ProviderCode, WalletPaymentProviders.Name(operation.ProviderCode),
        !string.IsNullOrWhiteSpace(operation.CustomerName));

    private static string Message(PaymentWalletOperation operation)
    {
        var guidance = StateMessage(operation).Replace("فلوسك", WalletPaymentProviders.Name(operation.ProviderCode), StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(operation.ProviderMessageAr)) return guidance;
        return operation.Status == "Failed" ? operation.ProviderMessageAr : guidance + "\n" + operation.ProviderMessageAr;
    }

    private static string StateMessage(PaymentWalletOperation operation) => operation.Status switch
    {
        "Succeeded" => "تم دفع قيمة الرحلة بنجاح عبر فلوسك.",
        "AwaitingConfirmation" => "تحقق من بيانات حسابك والمبلغ ثم أكد الدفع.",
        "AwaitingCode" => operation.NextAction switch
        {
            "Voucher" => "أدخل رقم القسيمة المطلوب من فلوسك لمتابعة العملية.",
            "Pin" => "أدخل رمز PIN المطلوب من فلوسك لمتابعة العملية.",
            _ => "أدخل رمز التحقق الذي أرسلته فلوسك إلى هاتفك."
        },
        "ProviderSucceeded" => "أكدت فلوسك الخصم وجارٍ تسجيل دفعتك. لا تكرر الدفع.",
        "Processing" => "جارٍ تنفيذ عملية الدفع. يرجى الانتظار.",
        "ReviewRequired" => "تعذر التأكد من نتيجة العملية. لا تكرر الدفع ولا تدفع نقداً؛ تواصل مع الدعم واذكر رقم العملية.",
        "Cancelled" => operation.ErrorCode == "session_expired" ? "انتهت جلسة الدفع. يمكنك بدء عملية جديدة." : "أُلغيت عملية الدفع قبل الخصم.",
        _ => operation.ErrorCode switch
        {
            "insufficient_funds" => "رصيد حسابك في فلوسك غير كافٍ للمبلغ والعمولة.",
            "provider_rejected" => "رفضت فلوسك العملية. تحقق من حسابك ثم حاول مجدداً.",
            "limit_exceeded" => "تجاوزت العملية حدود الدفع المسموح بها لدى فلوسك.",
            "provider_data_mismatch" => "تعذر التحقق من مطابقة بيانات الحساب والمبلغ لدى فلوسك.",
            "verification_failed" => "لم تقبل فلوسك بيانات التحقق. لم يُعتمد الدفع.",
            "session_expired" => "انتهت صلاحية عملية الدفع. ابدأ عملية جديدة.",
            _ => "تعذر إتمام خطوة الدفع عبر فلوسك. حاول مجدداً لاحقاً."
        }
    };

    private async Task NotifyAsync(PaymentWalletOperation operation)
    {
        if (realtime is null) return;
        try
        {
            var ride = await db.Rides.AsNoTracking().SingleAsync(x => x.Id == operation.RideId, CancellationToken.None);
            var recipients = ride.DriverId is int driverId ? new[] { ride.CustomerId, driverId } : new[] { ride.CustomerId };
            await realtime.PublishPaymentUpdatedAsync(recipients,
                new PaymentRealtimeEvent(ride.Id, "externalWalletPaymentUpdated", DateTime.UtcNow,
                    PaymentId: operation.PaymentTransactionId), CancellationToken.None);
        }
        catch (Exception) { /* A realtime outage never changes a persisted financial outcome. */ }
    }
}
