using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Payments;

public sealed record RideFinancialCaseModel(int? Id = null, int? RideId = null, string? Kind = null,
    string? Note = null, int? AssignedAdminId = null, string? EvidenceReference = null, string? Resolution = null, int? FundingLedgerAccountId = null, decimal? VerifiedAmount = null);

public sealed class RideFinancialCaseService(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user, IRideRealtimePublisher? realtime = null) : ModelService<RideFinancialCaseModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["list", "get", "add", "accept", "reject", "update"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, RideFinancialCaseModel model, CancellationToken ct)
    {
        var actor = user.RequireUserId();
        var admin = await db.Users.AnyAsync(x => x.Id == actor && x.IsActive && x.Role == UserRole.Admin, ct);
        if (admin) await YemenDrive.Application.Administration.AdministratorAccess.RequireAsync(db, actor, operation is "list" or "get" ? "refunds.read" : "refunds.write", ct);
        if (operation == "list")
        {
            var list = await db.RideFinancialCases.AsNoTracking().Where(x =>
            (admin || x.CustomerId == actor || x.DriverId == actor) && (model.RideId == null || x.RideId == model.RideId))
            .OrderByDescending(x => x.Id).Take(200).ToListAsync(ct);
            return list.Select(View).ToList();
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var item = model.Id is int id ? await db.RideFinancialCases.Include(x => x.Ride).SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        var rideId = item?.RideId ?? model.RideId ?? throw new ServiceException("ride_required", "حدد الرحلة المرتبطة بالمعالجة.");
        await RidePaymentGuard.LockAsync(db, rideId, ct);
        if (item != null) await db.Entry(item).ReloadAsync(ct);
        var ride = await db.Rides.SingleAsync(x => x.Id == rideId, ct);
        await db.Entry(ride).ReloadAsync(ct);
        if (!admin && actor != ride.CustomerId && actor != ride.DriverId) throw new ServiceException("case_access_denied", "هذه المعالجة لا تخصك.");
        if (operation == "add")
        {
            if (actor != ride.CustomerId || string.IsNullOrWhiteSpace(model.Note) || model.Note.Length > 10000) throw new ServiceException("dispute_reason_required", "اذكر سبب الاعتراض بوضوح.");
            item = await db.RideFinancialCases.SingleOrDefaultAsync(x => x.SourceReference == $"ride-receivable:{ride.Id}", ct);
            item ??= await db.RideFinancialCases.SingleOrDefaultAsync(x => x.SourceReference == $"ride-dispute:{ride.Id}", ct);
            item ??= new() { Ride = ride, RideId = ride.Id, CustomerId = ride.CustomerId, DriverId = ride.DriverId,
                Kind = "Dispute", SourceReference = $"ride-dispute:{ride.Id}", Amount = (await RideFinancialState.ReadAsync(db, ride, ct)).OutstandingAmount };
            if (item.Id == 0) db.RideFinancialCases.Add(item);
            item.Status = "Disputed"; item.Note = model.Note;
            item.AssignedAdminId = await OperationalReviewCoordinator.OwnerAsync(db, "refunds.write", ct);
        }
        if (item == null) throw new ServiceException("case_not_found", "المعالجة المالية غير موجودة.");
        if (operation == "get") { await tx.CommitAsync(ct); return View(item); }
        var closed = item.Status is "Resolved" or "Superseded";
        if (closed && operation != "add") throw new ServiceException("financial_decision_closed", "أُغلقت المعالجة؛ حدّث الحالة. لم ينفذ أي أثر مالي جديد.");
        if (!closed && operation is "accept" or "reject")
        {
            if (actor != ride.CustomerId) throw new ServiceException("customer_consent_required", "هذا القرار متاح للراكب صاحب المبلغ فقط.");
            if (item.Status is not ("AwaitingCashReceipt" or "AwaitingCustomer" or "AwaitingConsent")) throw new ServiceException("financial_decision_closed", "لم يعد هذا الخيار متاحاً؛ راجع المعالجة الحالية.");
            if (item.Kind is not ("CashChange" or "FareDecrease")) throw new ServiceException("case_requires_admin", "الاعتراض يحتاج قرار الإدارة.");
            if (operation == "reject") { item.Status = "ManualReview"; item.Note = model.Note ?? "رفض العميل التحويل للمحفظة أو اعترض على استلام النقد."; }
            else if (item.Status == "AwaitingCashReceipt")
            {
                if (item.Kind == "CashChange") await CashChangePostingAsync(db, item, false, actor, ct);
                else await RefundFareDifferenceAsync(item, actor, ct, returnByDriver: true);
                item.Status = "Resolved";
            }
            else if (item.Status is "AwaitingCustomer" or "AwaitingConsent")
            {
                if (item.Kind == "CashChange") await CashChangePostingAsync(db, item, true, actor, ct);
                else await RefundFareDifferenceAsync(item, actor, ct);
                item.Status = "Resolved";
            }
        }
        if (!closed && operation == "update")
        {
            if (actor == ride.CustomerId && item.Kind == "FareDecrease" && model.Resolution == "RequestCash" && item.Status == "AwaitingConsent")
            { item.Status = "AwaitingCashReturn"; item.Note = "اختار الراكب استلام فرق الأجرة نقداً؛ لا يسجل الاسترداد قبل تأكيد استلامه."; }
            else if (actor == ride.DriverId && model.Resolution == "CashReturned" && ((item.Kind == "CashChange" && item.Status == "AwaitingCustomer") || (item.Kind == "FareDecrease" && item.Status == "AwaitingCashReturn")))
            { item.Status = "AwaitingCashReceipt"; item.Note = "أفاد السائق بأنه أعاد الباقي نقداً؛ ننتظر تأكيد الراكب أو اعتراضه."; }
            else
            {
                if (!admin) throw new ServiceException("admin_required", "قرار المعالجة متاح للإدارة فقط.");
                if (string.IsNullOrWhiteSpace(model.Note)) throw new ServiceException("decision_reason_required", "اذكر سبب القرار والأدلة.");
                if (model.AssignedAdminId != null) await YemenDrive.Application.Administration.AdministratorAccess.RequireAsync(db, model.AssignedAdminId.Value, "refunds.write", ct);
                item.AssignedAdminId = model.AssignedAdminId ?? actor;
                item.Note = model.Note; item.EvidenceReference = model.EvidenceReference;
                if (model.Resolution == "ConfirmDebt" && item.Kind == "Receivable")
                {
                    var current = await RideFinancialState.ReadAsync(db, ride, ct);
                    if (current.ProviderPending || current.AmountUnderReview) throw new ServiceException("financial_basis_unresolved", "لا يثبت دين نهائي قبل حسم محاولة المزود أو اعتماد أجرة الإلغاء ومسؤوليتها.");
                    item.Status = "Confirmed";
                }
                else if (model.Resolution == "CashReceiptConfirmed" && item.Kind is "CashChange" or "FareDecrease")
                {
                    if (string.IsNullOrWhiteSpace(model.EvidenceReference)) throw new ServiceException("refund_evidence_required", "سجل مرجع الدليل الذي راجعته لإثبات استلام النقد.");
                    if (item.Kind == "CashChange") await CashChangePostingAsync(db, item, false, actor, ct);
                    else await RefundFareDifferenceAsync(item, actor, ct, returnByDriver: true);
                    item.Status = "Resolved";
                }
                else if (model.Resolution == "WaiveDebt" && item.Kind == "Receivable")
                {
                    ride.FinancialAmountDue = Math.Max(0m, RideFinancialState.Due(ride) - item.Amount);
                    item.Status = "Resolved";
                    await RideFinancialState.SynchronizeAsync(db, ride, actor, ct);
                }
                else if (model.Resolution == "VerifiedPayout" && item.Kind is "FareDecrease" or "CashChange")
                {
                    if (model.VerifiedAmount != item.Amount || model.FundingLedgerAccountId == null)
                        throw new ServiceException("verified_refund_amount_required", "المبلغ المحول فعلياً يجب أن يطابق الاستحقاق كاملاً مع مصدره ومرجع الدليل.");
                    var account = await VerifiedRefundPayout.ValidateAsync(db, actor, model.FundingLedgerAccountId.Value, item.Amount, model.EvidenceReference, ct);
                    if (item.Kind == "FareDecrease") await RefundFareDifferenceAsync(item, actor, ct, account.Id);
                    else
                    {
                        var party = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.User && x.EntityId == item.CustomerId, ct);
                        var payable = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == party.Id && x.Purpose == LedgerAccountPurpose.CustomerRefundPayable, ct);
                        await new AccountingPostingService(db).PostAsync(new(JournalEntryType.ManualAdjustment, item.SourceReference + ":returned", "إثبات تحويل الباقي النقدي للعميل بعد مراجعة دليل الاستلام", "YER",
                            [new(payable.Id, Debit: item.Amount, UserId: item.CustomerId, RideId: item.RideId), new(account.Id, Credit: item.Amount, RideId: item.RideId)], SourceType: "CashChange", SourceId: item.SourceReference + ":returned", CreatedByUserId: actor), ct);
                        // The driver's original cash holding remains owed to the company.
                    }
                    item.Status = "Resolved";
                }
                else if (model.Resolution is "ManualReview" or "Assign") item.Status = "ManualReview";
                else throw new ServiceException("invalid_financial_resolution", "القرار غير صالح لنوع المعالجة الحالي.");
            }
        }
        item.UpdatedAtUtc = DateTime.UtcNow;
        if (item.Status is "Disputed" or "ManualReview" && item.AssignedAdminId == null)
            item.AssignedAdminId = await db.Users.Where(x => x.IsActive && x.Role == UserRole.Admin && (x.IsSystemAdministrator || db.AdministratorPermissions.Any(p => p.UserId == x.Id && (p.Permission == "*" || p.Permission == "refunds.write")))).OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        item.DecidedByUserId = actor; item.DecidedAtUtc = DateTime.UtcNow;
        db.OperationalDecisionAudits.Add(new() { ActorUserId = actor, Subject = "RideFinancialCase", SubjectId = item.Id,
            Action = operation, Reason = model.Note ?? item.Note ?? "موافقة العميل على تسوية محددة" });
        await db.SaveChangesAsync(ct);
        var recipients = new[] { ride.CustomerId, ride.DriverId ?? ride.CustomerId, item.AssignedAdminId ?? 0 }.Where(x => x > 0).Distinct();
        foreach (var recipient in recipients) db.Notifications.Add(new Notification { UserId = recipient, Type = NotificationType.Payment,
            Title = item.Status == "Resolved" ? "اكتملت المعالجة المالية" : "تحديث معالجة مالية للرحلة", Body = $"الرحلة #{ride.Id}: المبلغ {item.Amount:0.##} ر.ي؛ {item.Note}",
            DataJson = $"{{\"action\":\"open_ride_payment\",\"rideId\":{ride.Id},\"financialCaseId\":{item.Id}}}" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        if (realtime != null) await realtime.PublishPaymentUpdatedAsync([ride.CustomerId, ride.DriverId ?? ride.CustomerId], new(ride.Id, "financialCaseUpdated", DateTime.UtcNow), ct);
        return View(item);
    }

    public static async Task CashChangePostingAsync(YemenDriveDbContext db, RideFinancialCase item, bool walletCredit, int actor, CancellationToken ct, bool hold = false)
    {
        var provision = new FinancialAccountProvisioningService(db);
        await provision.ProvisionUserAsync(await db.Users.Include(x => x.Wallet).SingleAsync(x => x.Id == item.CustomerId, ct), ct);
        await db.SaveChangesAsync(ct);
        var party = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.User && x.EntityId == item.CustomerId, ct);
        var payable = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == party.Id && x.Purpose == LedgerAccountPurpose.CustomerRefundPayable, ct);
        var driverParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.User && x.EntityId == item.DriverId, ct);
        var driverAccount = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == driverParty.Id && x.Purpose == LedgerAccountPurpose.DriverCurrentAccount, ct);
        var wallet = await db.Wallets.SingleAsync(x => x.UserId == item.CustomerId, ct);
        var walletParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.Wallet && x.EntityId == wallet.Id, ct);
        var walletAccount = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == walletParty.Id && x.Purpose == LedgerAccountPurpose.CustomerWallet, ct);
        var reference = item.SourceReference + (hold ? ":held" : ":returned");
        if (await db.JournalEntries.AnyAsync(x => x.SourceType == "CashChange" && x.SourceId == reference, ct)) return;
        await new AccountingPostingService(db).PostAsync(new(JournalEntryType.ManualAdjustment, reference, "الباقي النقدي المستحق للعميل وطريقة إعادته المعتمدة", "YER",
            hold ? [new(driverAccount.Id, Debit: item.Amount, FinancialPartyId: driverParty.Id, UserId: item.DriverId, RideId: item.RideId), new(payable.Id, Credit: item.Amount, FinancialPartyId: party.Id, UserId: item.CustomerId, RideId: item.RideId)] :
            [new(payable.Id, Debit: item.Amount, FinancialPartyId: party.Id, UserId: item.CustomerId, RideId: item.RideId), new(walletCredit ? walletAccount.Id : driverAccount.Id, Credit: item.Amount, FinancialPartyId: walletCredit ? walletParty.Id : driverParty.Id, RideId: item.RideId)],
            SourceType: "CashChange", SourceId: reference, CreatedByUserId: actor), ct);
        if (!hold && walletCredit)
        {
            wallet.Balance += item.Amount;
            db.WalletTransactions.Add(new() { WalletId = wallet.Id, RideId = item.RideId, Type = WalletTransactionType.Credit, Amount = item.Amount, BalanceAfter = wallet.Balance, ExternalReference = reference, Description = "إضافة الباقي النقدي باختيار العميل" });
            db.DriverSettlements.Add(new() { DriverId = item.DriverId!.Value, GrossRideAmount = 0, Adjustments = item.Amount, NetPayable = -item.Amount, Status = PaymentStatus.Pending, PaymentReference = reference });
        }
    }

    private async Task RefundFareDifferenceAsync(RideFinancialCase item, int actor, CancellationToken ct, int? fundingAccountId = null, bool returnByDriver = false)
    {
        var ride = item.Ride;
        var money = await RideFinancialState.ReadAsync(db, ride, ct);
        if (money.ProviderPending || await db.RideRouteChanges.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct))
            throw new ServiceException("financial_change_pending", "أكمل محاولة الدفع أو طلب تغيير الوجهة قبل تنفيذ الاسترداد.");
        if (item.Amount <= 0 || item.Amount > money.RefundDue) throw new ServiceException("refund_amount_changed", "تغير المبلغ المستحق؛ راجع الفاتورة الحالية.");
        var customerWallet = await db.Wallets.SingleAsync(x => x.UserId == ride.CustomerId, ct);
        var driverWallet = await db.Wallets.SingleAsync(x => x.UserId == ride.DriverId, ct);
        await RideFinancialState.SnapshotEntitlementAsync(db, ride, ct);
        var reversal = Math.Max(0m, ride.CollectedDriverEntitlement!.Value - RideFinancialState.DriverEntitlement(ride));
        var original = await db.PaymentTransactions.Where(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid).OrderBy(x => x.Id).FirstAsync(ct);
        var electronicReversal = await RideFinancialState.AllocateDriverReversalAsync(db, ride, reversal, ct);
        var debit = Math.Min(Math.Max(0, driverWallet.Balance), electronicReversal);
        driverWallet.Balance -= debit;
        var toWallet = fundingAccountId == null && !returnByDriver;
        if (toWallet) customerWallet.Balance += item.Amount;
        if (toWallet) db.WalletTransactions.Add(new() { WalletId = customerWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Refund, Amount = item.Amount, BalanceAfter = customerWallet.Balance, ExternalReference = item.SourceReference, Description = "استرداد فرق تخفيض أجرة الرحلة بموافقة العميل" });
        if (debit > 0) db.WalletTransactions.Add(new() { WalletId = driverWallet.Id, RideId = ride.Id, Type = WalletTransactionType.Debit, Amount = debit, BalanceAfter = driverWallet.Balance, ExternalReference = item.SourceReference, Description = "عكس المستحق غير المكتسب من فرق الأجرة" });
        var basis = await RideFinancialState.RefundAccountingBasisAsync(db, ride, ct);
        await new RideAccountingPostingService(db, new(db)).PostCancellationAsync(basis, original, item.Amount, toWallet, reversal, actor, ct, driverWalletDebit: debit, sourceReference: item.SourceReference, refundFundingAccountId: fundingAccountId,
            retainedServiceFee: Math.Min(ride.ServiceFee, Math.Max(0m, money.PaidAmount - money.RefundedAmount - item.Amount)));
        db.PaymentTransactions.Add(new() { UserId = ride.CustomerId, RideId = ride.Id, Amount = item.Amount, Currency = "YER", Provider = returnByDriver ? "CashFareDifferenceReturn" : toWallet ? "FareDifferenceRefundToWallet" : "VerifiedFareDifferencePayout", Status = PaymentStatus.Refunded, ProviderReference = item.SourceReference });
        var currentDelta = reversal - debit - (returnByDriver ? item.Amount : 0m);
        if (currentDelta != 0) db.DriverSettlements.Add(new() { DriverId = ride.DriverId!.Value, Adjustments = -currentDelta, NetPayable = -currentDelta, Status = currentDelta > 0 ? PaymentStatus.Pending : PaymentStatus.Refunded, PaymentReference = item.SourceReference + ":driver-current" });
        ride.CollectedDriverEntitlement -= reversal;
    }
    private static object View(RideFinancialCase x) => new { x.Id, x.RideId, x.CustomerId, x.DriverId, x.Kind, x.Status,
        x.Amount, x.Currency, x.SourceReference, x.AssignedAdminId, x.Note, x.EvidenceReference,
        x.DecidedByUserId, x.DecidedAtUtc, x.CreatedAtUtc, x.UpdatedAtUtc };
}
