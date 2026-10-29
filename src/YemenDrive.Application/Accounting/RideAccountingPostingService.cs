using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using DbLedgerAccount = YemenDrive.Database.Entities.LedgerAccount;

namespace YemenDrive.Application.Accounting;

/// <summary>
/// Translates a settled ride into the immutable double-entry structure agreed
/// for YemenDrive. Operational services call this while their own database
/// transaction is active, therefore payment, wallet movement and journal are
/// committed or rolled back together.
/// </summary>
public sealed class RideAccountingPostingService(
    YemenDriveDbContext db,
    AccountingPostingService posting)
{
    public async Task PostCashCollectionAsync(
        Ride ride,
        PaymentTransaction payment,
        decimal cashReceived,
        decimal walletAdjustment,
        int actorUserId,
        CancellationToken token)
    {
        var accounts = await ResolveAsync(ride, token);
        var fare = Fare(ride);
        var totalDue = ride.TotalAmount ?? fare + ride.ServiceFee;
        if (payment.Amount != totalDue)
            throw new ServiceException("accounting_payment_amount_mismatch", "لا يمكن ترحيل قيد لدفعة لا تطابق إجمالي الرحلة.");

        var lines = new List<AccountingPostingLine>();
        AddDebit(lines, accounts.DriverCurrentAccountId, cashReceived, accounts.DriverPartyId, ride.DriverId, ride.Id,
            "النقد المقبوض لدى السائق");
        if (walletAdjustment < 0)
            AddDebit(lines, accounts.CustomerWalletAccountId, Math.Abs(walletAdjustment), accounts.CustomerWalletPartyId, ride.CustomerId, ride.Id,
                "تغطية فرق الرحلة من محفظة العميل");
        AddCredit(lines, accounts.ServiceFeeAccountId, ride.ServiceFee, accounts.ServiceKindPartyId, null, ride.Id,
            "رسوم الخدمة المثبتة للرحلة");
        AddCredit(lines, accounts.ServiceCollectionAccountId, fare, accounts.ServiceKindPartyId, null, ride.Id,
            "تحصيل أجرة الخدمة المثبتة");
        if (walletAdjustment > 0)
            AddCredit(lines, accounts.CustomerWalletAccountId, walletAdjustment, accounts.CustomerWalletPartyId, ride.CustomerId, ride.Id,
                "إضافة الزيادة النقدية إلى محفظة العميل");
        AddDebit(lines, accounts.DriverCommissionAccountId, ride.DriverCommissionAmount, null, ride.DriverId, ride.Id,
            "عمولة السائق المثبتة للرحلة");
        AddCredit(lines, accounts.DriverCurrentAccountId, ride.DriverCommissionAmount, accounts.DriverPartyId, ride.DriverId, ride.Id,
            "إثبات عمولة السائق المستحقة للمنصة");

        await posting.PostAsync(new AccountingPostingRequest(
            JournalEntryType.RideCashCollection,
            $"CASH-RIDE-{ride.Id}",
            $"تحصيل نقدي للرحلة #{ride.Id}",
            payment.Currency,
            lines,
            SourceType: "RideCashPayment",
            SourceId: payment.ProviderReference ?? $"ride:{ride.Id}",
            IdempotencyKey: payment.IdempotencyKey,
            CreatedByUserId: actorUserId), token);
    }

    public async Task PostWalletPaymentAsync(
        Ride ride,
        PaymentTransaction payment,
        int actorUserId,
        CancellationToken token)
    {
        var accounts = await ResolveAsync(ride, token);
        var fare = Fare(ride);
        var totalDue = ride.TotalAmount ?? fare + ride.ServiceFee;
        if (payment.Amount != totalDue)
            throw new ServiceException("accounting_payment_amount_mismatch", "لا يمكن ترحيل قيد لدفعة لا تطابق إجمالي الرحلة.");

        var lines = new List<AccountingPostingLine>();
        AddDebit(lines, accounts.CustomerWalletAccountId, totalDue, accounts.CustomerWalletPartyId, ride.CustomerId, ride.Id,
            "دفع إجمالي الرحلة من محفظة العميل");
        AddCredit(lines, accounts.ServiceFeeAccountId, ride.ServiceFee, accounts.ServiceKindPartyId, null, ride.Id,
            "رسوم الخدمة المثبتة للرحلة");
        AddCredit(lines, accounts.ServiceCollectionAccountId, fare, accounts.ServiceKindPartyId, null, ride.Id,
            "تحصيل أجرة الخدمة المثبتة");
        AddDebit(lines, accounts.DriverCommissionAccountId, ride.DriverCommissionAmount, null, ride.DriverId, ride.Id,
            "عمولة السائق المثبتة للرحلة");
        AddCredit(lines, accounts.DriverCurrentAccountId, ride.DriverCommissionAmount, accounts.DriverPartyId, ride.DriverId, ride.Id,
            "إثبات عمولة السائق المستحقة للمنصة");

        await posting.PostAsync(new AccountingPostingRequest(
            JournalEntryType.RideWalletPayment,
            $"WALLET-RIDE-{ride.Id}",
            $"دفع من محفظة يمن درايف للرحلة #{ride.Id}",
            payment.Currency,
            lines,
            SourceType: "RideWalletPayment",
            SourceId: $"ride:{ride.Id}",
            IdempotencyKey: payment.IdempotencyKey,
            CreatedByUserId: actorUserId), token);
    }

    public async Task ValidateExternalWalletAccountsAsync(Ride ride, int paymentMethodId, CancellationToken token)
    {
        var accounts = await ResolveAsync(ride, token);
        var ids = new[] { accounts.DriverCurrentAccountId, accounts.ServiceFeeAccountId,
            accounts.ServiceCollectionAccountId, accounts.DriverCommissionAccountId };
        if (await db.LedgerAccounts.AnyAsync(x => ids.Contains(x.Id) && x.Currency != "YER", token))
            throw new ServiceException("ledger_currency_mismatch", "عملة أحد حسابات الرحلة ليست الريال اليمني.");
        var partyId = await PartyIdAsync(FinancialPartyType.ExternalProvider, paymentMethodId, token);
        if (!await db.LedgerAccounts.AnyAsync(x => x.FinancialPartyId == partyId &&
            x.Purpose == LedgerAccountPurpose.ExternalWallet && x.IsActive && x.IsPosting && x.Currency == "YER", token))
            throw new ServiceException("provider_ledger_not_found", "حساب تسوية طريقة الدفع غير مجهز بالريال اليمني.");
        _ = await DriverWalletAccountAsync(ride, token);
    }

    public async Task PostExternalWalletPaymentAsync(
        Ride ride,
        PaymentTransaction payment,
        int paymentMethodId,
        int actorUserId,
        CancellationToken token)
    {
        var accounts = await ResolveAsync(ride, token);
        var providerPartyId = await PartyIdAsync(FinancialPartyType.ExternalProvider, paymentMethodId, token);
        var providerAccountId = await db.LedgerAccounts.AsNoTracking()
            .Where(x => x.FinancialPartyId == providerPartyId && x.Purpose == LedgerAccountPurpose.ExternalWallet &&
                x.IsActive && x.IsPosting && x.Currency == payment.Currency)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(token)
            ?? throw new ServiceException("provider_ledger_not_found", "حساب تسوية طريقة الدفع غير مجهز.");
        var fare = Fare(ride);
        var totalDue = ride.TotalAmount ?? fare + ride.ServiceFee;
        if (payment.Amount != totalDue)
            throw new ServiceException("accounting_payment_amount_mismatch", "مبلغ المزود لا يطابق إجمالي الرحلة.");
        var driverWalletAccount = await DriverWalletAccountAsync(ride, token);
        var driverNet = ride.DriverShare ?? fare - ride.DriverCommissionAmount;

        var lines = new List<AccountingPostingLine>();
        AddDebit(lines, providerAccountId, totalDue, providerPartyId, ride.CustomerId, ride.Id,
            "تحصيل إجمالي الرحلة في حساب طريقة الدفع الخارجية");
        AddCredit(lines, accounts.ServiceFeeAccountId, ride.ServiceFee, accounts.ServiceKindPartyId, null, ride.Id,
            "رسوم الخدمة المثبتة للرحلة");
        AddCredit(lines, accounts.ServiceCollectionAccountId, fare, accounts.ServiceKindPartyId, null, ride.Id,
            "تحصيل أجرة الخدمة المثبتة");
        // The provider holds the collection; the driver has no cash receivable.
        // Recognize the actual driver entitlement and reconcile the wallet liability to its credit.
        AddDebit(lines, accounts.DriverCommissionAccountId, driverNet, null, ride.DriverId, ride.Id,
            "إثبات صافي مستحق السائق من الرحلة المدفوعة إلكترونياً");
        AddCredit(lines, driverWalletAccount.Id, driverNet, driverWalletAccount.FinancialPartyId, ride.DriverId, ride.Id,
            "إضافة صافي مستحق السائق إلى التزام محفظته");
        await posting.PostAsync(new AccountingPostingRequest(
            JournalEntryType.RideWalletPayment, $"EXTERNAL-RIDE-{ride.Id}",
            $"دفع الرحلة #{ride.Id} عبر {payment.Provider}", payment.Currency, lines,
            SourceType: "RideExternalWalletPayment", SourceId: $"ride:{ride.Id}",
            IdempotencyKey: payment.IdempotencyKey, CreatedByUserId: actorUserId), token);
    }

    private async Task<DbLedgerAccount> DriverWalletAccountAsync(Ride ride, CancellationToken token)
    {
        var walletId = await db.Wallets.Where(x => x.UserId == ride.DriverId)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(token)
            ?? throw new ServiceException("driver_wallet_not_found", "محفظة السائق غير موجودة.");
        var partyId = await PartyIdAsync(FinancialPartyType.Wallet, walletId, token);
        return await db.LedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.FinancialPartyId == partyId &&
            x.Purpose == LedgerAccountPurpose.CustomerWallet && x.IsActive && x.IsPosting && x.Currency == "YER", token)
            ?? throw new ServiceException("driver_wallet_ledger_not_found", "حساب محفظة السائق المحاسبي غير مجهز.");
    }

    public async Task PostCancellationAsync(
        Ride ride,
        PaymentTransaction originalPayment,
        decimal refundAmount,
        bool creditCustomerWallet,
        decimal driverCommissionReversal,
        int actorUserId,
        CancellationToken token)
    {
        if (refundAmount <= 0) return;
        var accounts = await ResolveAsync(ride, token);
        var lines = new List<AccountingPostingLine>();
        var fare = Fare(ride);
        var collectionReversal = Math.Min(fare, refundAmount);
        var feeReversal = refundAmount - collectionReversal;
        // The cancellation fee remains in its original service revenue account.
        AddDebit(lines, accounts.ServiceCollectionAccountId, collectionReversal, accounts.ServiceKindPartyId, null, ride.Id,
            "عكس تحصيل الخدمة بسبب إلغاء الرحلة");
        AddDebit(lines, accounts.ServiceFeeAccountId, feeReversal, accounts.ServiceKindPartyId, null, ride.Id,
            "عكس الجزء المسترد من رسوم الخدمة");
        if (creditCustomerWallet)
            AddCredit(lines, accounts.CustomerWalletAccountId, refundAmount, accounts.CustomerWalletPartyId, ride.CustomerId, ride.Id,
                "إضافة استرداد الإلغاء إلى محفظة العميل");
        else
            AddCredit(lines, accounts.DriverCurrentAccountId, refundAmount, accounts.DriverPartyId, ride.DriverId, ride.Id,
                "إثبات استرداد العميل للنقد مباشرة من السائق");
        AddDebit(lines, accounts.DriverCurrentAccountId, driverCommissionReversal, accounts.DriverPartyId, ride.DriverId, ride.Id,
            "عكس المبلغ المثبت للسائق بسبب الإلغاء");
        AddCredit(lines, accounts.DriverCommissionAccountId, driverCommissionReversal, null, ride.DriverId, ride.Id,
            "عكس عمولة السائق بسبب الإلغاء");

        var method = creditCustomerWallet ? "wallet" : "driver-cash";
        await posting.PostAsync(new AccountingPostingRequest(
            JournalEntryType.RideCancellation,
            $"CANCEL-RIDE-{ride.Id}-{method}",
            $"إلغاء رحلة #{ride.Id} بعد دفع {originalPayment.Provider}",
            originalPayment.Currency,
            lines,
            SourceType: "RideCancellation",
            SourceId: $"payment:{originalPayment.Id}:{method}",
            CreatedByUserId: actorUserId), token);
    }

    private async Task<RideAccounts> ResolveAsync(Ride ride, CancellationToken token)
    {
        if (ride.DriverId is null)
            throw new ServiceException("driver_not_assigned", "لا يمكن ترحيل قيد رحلة بلا سائق مسند.");
        var customerWalletId = await db.Wallets.Where(x => x.UserId == ride.CustomerId)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(token)
            ?? throw new ServiceException("wallet_not_found", "محفظة العميل غير موجودة.");
        var driverPartyId = await PartyIdAsync(FinancialPartyType.User, ride.DriverId.Value, token);
        var customerWalletPartyId = await PartyIdAsync(FinancialPartyType.Wallet, customerWalletId, token);
        var serviceKindPartyId = await PartyIdAsync(FinancialPartyType.ServiceKind, ride.ServiceKindId, token);
        var accountRows = await db.LedgerAccounts.AsNoTracking()
            .Where(x => x.IsActive && x.IsPosting &&
                (x.Code == "5100" ||
                 (x.FinancialPartyId == driverPartyId && x.Purpose == LedgerAccountPurpose.DriverCurrentAccount) ||
                 (x.FinancialPartyId == customerWalletPartyId && x.Purpose == LedgerAccountPurpose.CustomerWallet) ||
                 (x.FinancialPartyId == serviceKindPartyId &&
                  (x.Purpose == LedgerAccountPurpose.ServiceFee || x.Purpose == LedgerAccountPurpose.ServiceCollection))))
            .ToListAsync(token);
        int Required(Func<DbLedgerAccount, bool> predicate, string code) => accountRows.FirstOrDefault(predicate)?.Id
            ?? throw new ServiceException("ledger_account_not_found", $"الحساب المالي المطلوب ({code}) غير مجهز للرحلة.");
        return new RideAccounts(
            Required(x => x.FinancialPartyId == driverPartyId && x.Purpose == LedgerAccountPurpose.DriverCurrentAccount, "حساب السائق"),
            Required(x => x.FinancialPartyId == customerWalletPartyId && x.Purpose == LedgerAccountPurpose.CustomerWallet, "محفظة العميل"),
            Required(x => x.FinancialPartyId == serviceKindPartyId && x.Purpose == LedgerAccountPurpose.ServiceFee, "رسوم الخدمة"),
            Required(x => x.FinancialPartyId == serviceKindPartyId && x.Purpose == LedgerAccountPurpose.ServiceCollection, "تحصيل الخدمة"),
            Required(x => x.Code == "5100", "عمولات السائقين"),
            driverPartyId,
            customerWalletPartyId,
            serviceKindPartyId);
    }

    private async Task<int> PartyIdAsync(FinancialPartyType type, int entityId, CancellationToken token) =>
        await db.FinancialParties.AsNoTracking().Where(x => x.Type == type && x.EntityId == entityId && x.IsActive)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(token)
        ?? throw new ServiceException("financial_party_not_found", "إحدى الجهات المالية للرحلة غير مجهزة.");

    private static decimal Fare(Ride ride) => (ride.CustomerPrice ?? ride.ServerPrice
        ?? throw new ServiceException("fare_not_set", "لا يمكن ترحيل قيد رحلة بلا سعر مثبت.")) + ride.WaitingFee;

    private static void AddDebit(List<AccountingPostingLine> lines, int accountId, decimal amount,
        int? partyId, int? userId, int rideId, string description)
    {
        if (amount > 0) lines.Add(new AccountingPostingLine(accountId, Debit: amount,
            FinancialPartyId: partyId, UserId: userId, RideId: rideId, Description: description));
    }

    private static void AddCredit(List<AccountingPostingLine> lines, int accountId, decimal amount,
        int? partyId, int? userId, int rideId, string description)
    {
        if (amount > 0) lines.Add(new AccountingPostingLine(accountId, Credit: amount,
            FinancialPartyId: partyId, UserId: userId, RideId: rideId, Description: description));
    }

    private sealed record RideAccounts(
        int DriverCurrentAccountId,
        int CustomerWalletAccountId,
        int ServiceFeeAccountId,
        int ServiceCollectionAccountId,
        int DriverCommissionAccountId,
        int DriverPartyId,
        int CustomerWalletPartyId,
        int ServiceKindPartyId);
}
