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

public sealed record WalletPaymentView(int Id, int? RideId, int PaymentMethodId, string Status, string NextAction,
    string? CustomerName, string Mobile, decimal Amount, decimal? Fees, bool FeesKnown, string Currency,
    string FeeCurrency, string Recipient, string? ProviderReference, string Message, DateTime? PaidAtUtc,
    string ProviderCode, string ProviderName, bool CustomerIsProviderVerified, bool CanCancel,
    string RequestId = "", WalletTopUpReceipt? Receipt = null);

public sealed record WalletTopUpReceipt(string Number, decimal Amount, string Currency, decimal? ProviderFees,
    bool FeesKnown, string ProviderName, string? CustomerName, string Mobile, string RequestId, string? ProviderReference,
    DateTime? CompletedAtUtc, decimal? BalanceAfter, int? WalletTransactionId, int? JournalEntryId);

public sealed class WalletPaymentService(YemenDriveDbContext db, ICurrentUserContext currentUser,
    IWalletPaymentGateway gateway, ExternalWalletPaymentSettlement settlement,
    IRideRealtimePublisher? realtime = null, ExternalWalletTopUpSettlement? topUps = null)
{
    private ExternalWalletTopUpSettlement TopUps => topUps ?? throw new ServiceException("top_up_unavailable", "خدمة الشحن غير مجهزة.");

    private async Task<User> RequireTopUpCustomerAsync(CancellationToken token) =>
        await db.Users.SingleOrDefaultAsync(x => x.Id == currentUser.RequireUserId() && x.IsActive && x.Role == UserRole.Customer, token)
        ?? throw new ServiceException("wallet_access_denied", "الشحن متاح لحساب الراكب فقط.");

    private async Task RequireScopeAsync(int? rideId, CancellationToken token)
    {
        if (rideId is int id) await RequireCustomerAsync(id, token);
        else await RequireTopUpCustomerAsync(token);
    }

    private async Task LockScopeAsync(int? rideId, int customerId, CancellationToken token)
    {
        if (rideId is int id) { await RidePaymentGuard.LockAsync(db, id, token); return; }
        var wallet = await db.Wallets.FromSqlInterpolated(
            $"SELECT * FROM [Wallets] WITH (UPDLOCK,HOLDLOCK) WHERE [UserId] = {customerId}").SingleOrDefaultAsync(token);
        if (wallet == null) throw new ServiceException("wallet_not_found", "محفظتك غير مجهزة.");
    }

    public async Task<WalletPaymentView?> GetTopUpAsync(int? paymentId, CancellationToken token, Guid? requestId = null)
    {
        var customer = await RequireTopUpCustomerAsync(token);
        var query = db.PaymentWalletOperations.Where(x => x.RideId == null && x.CustomerId == customer.Id);
        if (paymentId != null) query = query.Where(x => x.Id == paymentId);
        else if (requestId != null) query = query.Where(x => x.RequestId == requestId.Value.ToString("N"));
        else query = query.Where(x => x.IsOpen);
        var operation = await query.OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
        if (operation == null) return null;
        await RecoverAsync(operation, token);
        return View(operation);
    }

    public async Task<WalletPaymentView> StartTopUpAsync(int methodId, decimal amount, Guid requestId, CancellationToken token)
    {
        var customer = await RequireTopUpCustomerAsync(token);
        if (amount <= 0 || amount > 9999999999999999m || decimal.Round(amount, 2) != amount || requestId == Guid.Empty)
            throw new ServiceException("invalid_top_up_amount", "أدخل مبلغ شحن صحيحاً أكبر من صفر بمنزلتين عشريتين كحد أقصى.");
        PaymentWalletOperation operation;
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
        {
            await LockScopeAsync(null, customer.Id, token);
            var reference = requestId.ToString("N");
            var same = await db.PaymentWalletOperations.SingleOrDefaultAsync(x => x.RequestId == reference, token);
            if (same != null && (same.CustomerId != customer.Id || same.RideId != null || same.Amount != amount || same.PaymentMethodId != methodId))
                throw new ServiceException("top_up_reference_conflict", "رقم المرجع مرتبط بعملية أخرى. استكمل العملية الأصلية.");
            var existing = same ?? await db.PaymentWalletOperations.SingleOrDefaultAsync(x => x.CustomerId == customer.Id && x.RideId == null && x.IsOpen, token);
            if (existing != null)
            {
                await tx.CommitAsync(token);
                await RecoverAsync(existing, token);
                return View(existing);
            }
            var method = await db.PaymentMethods.SingleOrDefaultAsync(x => x.Id == methodId, token);
            var account = await db.PaymentProviderAccounts.SingleOrDefaultAsync(x => x.PaymentMethodId == methodId, token);
            if (method == null || !method.IsActive || !method.IsAvailableForWalletTopUp || method.Kind != PaymentMethodKind.ExternalWallet ||
                !WalletPaymentProviders.IsSupported(method.ProviderCode) || account == null || !account.IsEnabled ||
                account.ProviderCode != method.ProviderCode || !gateway.IsReady(methodId, account.ConfigurationVersion))
                throw new ServiceException("wallet_payment_unavailable", "خدمة هذه المحفظة غير متاحة حالياً. اختر طريقة أخرى.");
            await TopUps.ValidateAsync(customer.Id, methodId, token);
            operation = new PaymentWalletOperation { CustomerId = customer.Id, PaymentMethodId = methodId,
                ProviderCode = method.ProviderCode, RequestId = reference, ConfigurationVersion = account.ConfigurationVersion,
                RuntimeInstanceId = gateway.InstanceId, Mobile = NormalizeMobile(customer.PhoneNumber, method.ProviderCode),
                Amount = amount, UpdatedAtUtc = DateTime.UtcNow };
            db.PaymentWalletOperations.Add(operation);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }
        return await RunProviderAsync(operation, ct => gateway.RequestAsync(operation.PaymentMethodId,
            operation.ConfigurationVersion, operation.ProviderCode, operation.RequestId, operation.Mobile, operation.Amount, ct));
    }

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
                 (x.Status == RideCancellationStatus.CustomerReviewPending || x.Status == RideCancellationStatus.RefundMethodPending || x.Status == RideCancellationStatus.RefundConsentPending)), token))
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
                // A fresh withdrawal gets a fresh reference; Confirm/Complete keep this stored value.
                RequestId = Guid.NewGuid().ToString("N"), ConfigurationVersion = account.ConfigurationVersion,
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

    public async Task<WalletPaymentView> ConfirmAsync(int? rideId, int paymentId, bool approved, CancellationToken token)
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

    public async Task<WalletPaymentView> CompleteAsync(int? rideId, int paymentId, string? code, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 256)
            throw new ServiceException("verification_code_required", "أدخل الرمز أو رقم القسيمة المطلوب من المحفظة المختارة.");
        await RequireScopeAsync(rideId, token);
        var provider = await db.PaymentWalletOperations.Where(x => x.Id == paymentId && x.RideId == rideId)
            .Where(x => x.CustomerId == currentUser.RequireUserId())
            .Select(x => x.ProviderCode).SingleOrDefaultAsync(token)
            ?? throw new ServiceException("wallet_payment_not_found", "عملية الدفع غير موجودة.");
        var verificationCode = provider == "Floosak" ? NormalizeFloosakOtp(code) : code.Trim();
        var claimed = await ClaimAsync(rideId, paymentId, "Complete", token);
        var operation = claimed.Operation;
        if (!claimed.Claimed) return View(operation);
        // This value exists only in the call stack, never in an entity, log or response.
        return await RunProviderAsync(operation, ct => gateway.CompleteAsync(operation.PaymentMethodId,
            operation.ConfigurationVersion, operation.ProviderCode, operation.ProviderOperationId!, verificationCode, ct), verificationCode);
    }

    public static string NormalizeFloosakOtp(string value)
    {
        var digits = new StringBuilder();
        foreach (var character in value.Trim())
        {
            if (!char.IsDigit(character))
                throw new ServiceException("invalid_verification_code", "رمز فلوسك يجب أن يحتوي على أرقام فقط. أدخل الرمز كما وصل إلى هاتفك.");
            digits.Append((int)char.GetNumericValue(character));
        }
        if (!int.TryParse(digits.ToString(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new ServiceException("invalid_verification_code", "رمز فلوسك غير صالح. أدخل الرمز الرقمي كما وصل إلى هاتفك.");
        return digits.ToString();
    }

    private async Task<(PaymentWalletOperation Operation, bool Claimed)> ClaimAsync(int? rideId, int paymentId,
        string stage, CancellationToken token)
    {
        await RequireScopeAsync(rideId, token);
        var operation = await db.PaymentWalletOperations.SingleOrDefaultAsync(x => x.Id == paymentId && x.RideId == rideId && x.CustomerId == currentUser.RequireUserId(), token)
            ?? throw new ServiceException("wallet_payment_not_found", "عملية الدفع غير موجودة.");
        await RecoverAsync(operation, token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await LockScopeAsync(rideId, operation.CustomerId, token);
        await db.Entry(operation).ReloadAsync(token);
        var canClaim = stage == "Cancel"
            ? WalletPaymentCancellationPolicy.CanCancel(operation)
            : stage == "Confirm" ? operation.Status == "AwaitingConfirmation" : operation.Status == "AwaitingCode";
        if (!canClaim) { await tx.CommitAsync(token); return (operation, false); }
        if (stage != "Cancel")
        {
            var accountValid = await db.PaymentProviderAccounts.AsNoTracking().AnyAsync(x =>
                x.PaymentMethodId == operation.PaymentMethodId && x.IsEnabled && x.ProviderCode == operation.ProviderCode &&
                x.ConfigurationVersion == operation.ConfigurationVersion && x.PaymentMethod.IsActive &&
                (rideId != null ? x.PaymentMethod.IsAvailableForRidePayment : x.PaymentMethod.IsAvailableForWalletTopUp) && x.PaymentMethod.ProviderCode == operation.ProviderCode, token);
            if (!accountValid)
            {
                SetTerminal(operation, "Cancelled", "session_expired");
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                await NotifyAsync(operation);
                return (operation, false);
            }
            if (rideId is int actualRideId)
            {
            var ride = await db.Rides.SingleAsync(x => x.Id == actualRideId, token);
            EnsureRideReady(ride);
            var paymentMethodCode = await db.PaymentMethods.AsNoTracking()
                .Where(x => x.Id == operation.PaymentMethodId).Select(x => x.Code).SingleAsync(token);
            RidePaymentPolicy.EnsureReady(ride, paymentMethodCode);
            if (AmountDue(ride) != operation.Amount ||
                await db.PaymentTransactions.AnyAsync(x => x.RideId == rideId && x.Status == PaymentStatus.Paid, token))
                throw new ServiceException("wallet_payment_ride_changed", "تغيرت حالة دفع الرحلة. حدّث بيانات الرحلة قبل المتابعة.");
            await settlement.ValidateAsync(ride, operation.PaymentMethodId, token);
            }
            else await TopUps.ValidateAsync(operation.CustomerId, operation.PaymentMethodId, token);
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
        await LockScopeAsync(operation.RideId, operation.CustomerId, token);
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
        try
        {
            if (operation.RideId == null) await TopUps.SettleAsync(operation, CancellationToken.None);
            else await settlement.SettleAsync(operation, CancellationToken.None);
        }
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
        // Invalid code syntax can be rejected locally by the library without submitting HTTP.
        // Its retained OTP action and matching workflow identify a correction within the same attempt.
        if (operation.ProviderCode == "Floosak" && operation.Stage == "Complete" &&
            result.Status == OperationStatus.Failed && result.ErrorCode == WalletErrorCode.InvalidRequest &&
            result.NextAction == WalletStepNextAction.OneTimePassword && result.OperationId == operation.ProviderOperationId &&
            !string.IsNullOrWhiteSpace(result.OperationId) && result.RequestId == operation.RequestId &&
            result.ExpiresAt > DateTimeOffset.UtcNow && ValidCustomer(operation, result.Customer) &&
            result.Receipt is { CompletedAt: null } retryReceipt && retryReceipt.Amount.Amount == operation.Amount &&
            retryReceipt.Amount.Currency == CurrencyCode.YER && retryReceipt.Reference.ClientRequestId.Value == operation.RequestId)
        {
            operation.Status = "AwaitingCode";
            operation.NextAction = "OneTimePassword";
            operation.ErrorCode = "verification_failed";
            return;
        }
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
            if (receipt.Fees is { } finalFees) { operation.Fees = finalFees.Amount; operation.FeesKnown = true; }
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
        // Failed is the SDK's definitive outcome, including provider error codes unknown to this app.
        // The SDK reports uncertainty after dispatch as Unknown/Pending, so message text and error-code
        // allowlists must not turn a rejected withdrawal into an indefinitely reserved payment.
        var definitiveFailure = result.Status == OperationStatus.Failed && result.Receipt?.CompletedAt is null;
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
        !string.IsNullOrWhiteSpace(operation.CustomerName), WalletPaymentCancellationPolicy.CanCancel(operation), operation.RequestId,
        operation.RideId == null && operation.Status == "Succeeded" ? new WalletTopUpReceipt($"TOPUP-{operation.Id}",
            operation.Amount, operation.Currency, operation.Fees, operation.FeesKnown, WalletPaymentProviders.Name(operation.ProviderCode),
            operation.CustomerName, operation.Mobile, operation.RequestId, operation.ProviderReference, operation.PaidAtUtc,
            operation.BalanceAfter, operation.WalletTransactionId, operation.JournalEntryId) : null);

    private static string Message(PaymentWalletOperation operation)
    {
        var guidance = StateMessage(operation).Replace("فلوسك", WalletPaymentProviders.Name(operation.ProviderCode), StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(operation.ProviderMessageAr)) return guidance;
        return operation.Status == "Failed" ? operation.ProviderMessageAr : guidance + "\n" + operation.ProviderMessageAr;
    }

    private static string StateMessage(PaymentWalletOperation operation) => operation.Status switch
    {
        "Succeeded" => operation.RideId == null ? "تم شحن محفظتك بنجاح عبر فلوسك." : "تم دفع قيمة الرحلة بنجاح عبر فلوسك.",
        "AwaitingConfirmation" => "تحقق من بيانات حسابك والمبلغ ثم أكد الدفع.",
        "AwaitingCode" => operation.NextAction switch
        {
            "Voucher" => "أدخل رقم القسيمة المطلوب من فلوسك لمتابعة العملية.",
            "Pin" => "أدخل رمز PIN المطلوب من فلوسك لمتابعة العملية.",
            _ => "أدخل رمز التحقق الذي أرسلته فلوسك إلى هاتفك."
        },
        "ProviderSucceeded" => "أكدت فلوسك الخصم وجارٍ تسجيل دفعتك. لا تكرر الدفع.",
        "Processing" => "جارٍ تنفيذ عملية الدفع. يرجى الانتظار.",
        "ReviewRequired" when WalletPaymentCancellationPolicy.CanCancel(operation) => "تعذر تحضير بيانات حساب فلوسك قبل إرسال رمز التحقق. يمكنك إلغاء هذه المحاولة واختيار طريقة دفع أخرى.",
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
        if (realtime is null || operation.RideId == null) return;
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
