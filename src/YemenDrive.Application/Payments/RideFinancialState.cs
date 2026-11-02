using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Rides;
using Ride = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Payments;

public sealed record RideMoneyView(decimal AmountDue, decimal PaidAmount, decimal RefundedAmount,
    decimal OutstandingAmount, decimal RefundDue, bool PaymentCompleted, bool ProviderPending, bool AmountUnderReview = false);

/// <summary>Actual receipts, operational closure and financial review are independent facts.</summary>
public static class RideFinancialState
{
    public static async Task<IReadOnlyDictionary<int, RideMoneyView>> ReadManyAsync(YemenDriveDbContext db, IReadOnlyCollection<Ride> rides, CancellationToken ct)
    {
        var ids = rides.Select(x => x.Id).ToArray();
        var receipts = await db.PaymentTransactions.AsNoTracking().Where(x => x.RideId != null && ids.Contains(x.RideId.Value) &&
            (x.Status == PaymentStatus.Paid || x.Status == PaymentStatus.Refunded)).Select(x => new { x.RideId, x.Status, x.Amount }).ToListAsync(ct);
        var pending = await db.PaymentWalletOperations.AsNoTracking().Where(x => x.RideId != null && ids.Contains(x.RideId.Value) && x.IsOpen).Select(x => x.RideId!.Value).Distinct().ToListAsync(ct);
        var changes = await db.RideRouteChanges.AsNoTracking().Where(x => ids.Contains(x.RideId) && x.IsOpen && x.Status == "awaiting_payment").ToDictionaryAsync(x => x.RideId, x => x.NewTotal, ct);
        return rides.ToDictionary(x => x.Id, x =>
        {
            var paid = receipts.Where(p => p.RideId == x.Id && p.Status == PaymentStatus.Paid).Sum(p => p.Amount);
            var refunded = receipts.Where(p => p.RideId == x.Id && p.Status == PaymentStatus.Refunded).Sum(p => p.Amount);
            return Money(x.ServiceEndedAtUtc == null && changes.TryGetValue(x.Id, out var proposed) ? proposed : Due(x), paid, refunded, pending.Contains(x.Id), x.Status == RideStatus.Cancelled && x.FinancialAmountDue == null);
        });
    }

    private static RideMoneyView Money(decimal due, decimal paid, decimal refunded, bool pending, bool review = false) => new(due, paid, refunded,
        Math.Max(0m, due - paid + refunded), Math.Max(0m, paid - refunded - due), paid - refunded >= due && !pending && !review, pending, review);
    public static decimal Due(Ride ride) => Math.Max(0m, ride.FinancialAmountDue ?? ride.TotalAmount ??
        (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) + ride.ServiceFee + ride.WaitingFee);

    public static decimal DriverEntitlement(Ride ride) => Math.Max(0m, ride.FinancialDriverEntitlement ??
        ride.DriverShare ?? Due(ride) - ride.ServiceFee - ride.DriverCommissionAmount);

    public static async Task<RideMoneyView> ReadAsync(YemenDriveDbContext db, Ride ride, CancellationToken ct)
    {
        var receipts = await db.PaymentTransactions.AsNoTracking().Where(x => x.RideId == ride.Id &&
            (x.Status == PaymentStatus.Paid || x.Status == PaymentStatus.Refunded)).ToListAsync(ct);
        var paid = receipts.Where(x => x.Status == PaymentStatus.Paid).Sum(x => x.Amount);
        var refunded = receipts.Where(x => x.Status == PaymentStatus.Refunded).Sum(x => x.Amount);
        var adjustment = await RideRouteSettlement.PendingAsync(db, ride, ct);
        var due = adjustment?.NewTotal ?? Due(ride);
        var pending = await db.PaymentWalletOperations.AnyAsync(x => x.RideId == ride.Id && x.IsOpen, ct);
        return Money(due, paid, refunded, pending, ride.Status == RideStatus.Cancelled && ride.FinancialAmountDue == null);
    }

    public static async Task EnsureNoConfirmedDebtAsync(YemenDriveDbContext db, int customerId, CancellationToken ct)
    {
        if (await db.RideFinancialCases.AnyAsync(x => x.CustomerId == customerId && x.Kind == "Receivable" &&
            x.Amount > 0 && (x.Status == "Open" || x.Status == "Confirmed"), ct))
            throw new ServiceException("outstanding_payment_required", "لديك مبلغ رحلة مستحق غير مسدد. سدده من تفاصيل الرحلة قبل طلب رحلة جديدة؛ يمكنك تقديم اعتراض للمراجعة.");
    }

    public static async Task SnapshotEntitlementAsync(YemenDriveDbContext db, Ride ride, CancellationToken ct)
    {
        if (ride.CollectedDriverEntitlement != null) return;
        var payments = await db.PaymentTransactions.AsNoTracking().Where(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid).ToListAsync(ct);
        ride.CollectedDriverEntitlement = payments.Count == 0 ? 0m : payments.All(x => x.DriverNetAmount != null)
            ? payments.Sum(x => x.DriverNetAmount ?? 0m) : Math.Max(0m, ride.DriverShare ?? Due(ride) - ride.ServiceFee - ride.DriverCommissionAmount);
    }

    // A detached allocation is used only to translate a receipt into ledger lines.
    // It never replaces or reprices the historical ride.
    public static async Task<Ride> AllocateReceiptAsync(YemenDriveDbContext db, Ride ride, PaymentTransaction payment, CancellationToken ct, bool providerConfirmed = false)
    {
        await SnapshotEntitlementAsync(db, ride, ct);
        var money = await ReadAsync(db, ride, ct);
        if (payment.Amount <= 0 || !providerConfirmed && payment.Amount > money.OutstandingAmount)
            throw new ServiceException("invalid_payment_amount", "المبلغ لا يطابق المتبقي المؤكد للرحلة.");
        var adjustment = await RideRouteSettlement.PendingAsync(db, ride, ct);
        var total = money.AmountDue;
        var netPaid = money.PaidAmount - money.RefundedAmount;
        var target = adjustment == null ? DriverEntitlement(ride) : RideRouteSettlement.ProjectedDriverEntitlement(ride, adjustment);
        var entitlement = total == 0 ? 0 : decimal.Round(target * Math.Min(total, netPaid + payment.Amount) / total, 2);
        var driverNet = Math.Clamp(entitlement - ride.CollectedDriverEntitlement!.Value, 0m, payment.Amount);
        var earlier = await db.PaymentTransactions.AsNoTracking().Where(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid).ToListAsync(ct);
        var earlierFee = earlier.Count == 0 ? 0m : earlier.All(x => x.ServiceFeeAmount != null) ? earlier.Sum(x => x.ServiceFeeAmount ?? 0m) : ride.ServiceFee;
        var fee = total == 0 ? 0 : Math.Clamp(decimal.Round(Math.Min(ride.ServiceFee, total) * Math.Min(total, netPaid + payment.Amount) / total, 2) - earlierFee, 0m, payment.Amount - driverNet);
        payment.ServiceFeeAmount = fee;
        payment.DriverNetAmount = driverNet;
        payment.IsSupplemental = money.PaidAmount > 0 || payment.Amount < total;
        ride.CollectedDriverEntitlement += driverNet;
        return new Ride { Id = ride.Id, CustomerId = ride.CustomerId, DriverId = ride.DriverId,
            ServiceKindId = ride.ServiceKindId, ServiceCatalogItemId = ride.ServiceCatalogItemId,
            CustomerPrice = payment.Amount - fee, TotalAmount = payment.Amount, ServiceFee = fee,
            DriverShare = driverNet, DriverCommissionAmount = payment.Amount - fee - driverNet,
            PlatformShare = payment.Amount - driverNet };
    }

    public static async Task<decimal> AllocateDriverReversalAsync(YemenDriveDbContext db, Ride ride, decimal reversal, CancellationToken ct)
    {
        // Consume each original earning once, newest receipt first. Cash and electronic
        // receipts hold different assets; a prior refund must not debit either twice.
        var receipts = await db.PaymentTransactions.Where(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid).OrderByDescending(x => x.Id).ToListAsync(ct);
        var electronic = 0m;
        var remaining = reversal;
        foreach (var p in receipts)
        {
            var earned = p.DriverNetAmount ?? (receipts.Count == 1 ? (ride.CollectedDriverEntitlement ?? 0m) : 0m);
            var take = Math.Min(remaining, Math.Max(0, earned - (p.DriverEntitlementReversed ?? 0m)));
            p.DriverEntitlementReversed = (p.DriverEntitlementReversed ?? 0m) + take;
            if (p.Provider != "Cash") electronic += take;
            remaining -= take;
            if (remaining <= 0) break;
        }
        if (remaining > 0) throw new ServiceException("historical_entitlement_review_required", "مستحقات الدفعات القديمة المختلطة تحتاج تسوية موثقة قبل عكسها؛ لم يتم خصم أي مبلغ.");
        return electronic;
    }

    public static async Task<Ride> RefundAccountingBasisAsync(YemenDriveDbContext db, Ride ride, CancellationToken ct)
    {
        var receipts = await db.PaymentTransactions.AsNoTracking().Where(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid).ToListAsync(ct);
        var amount = receipts.Sum(x => x.Amount);
        var fees = receipts.All(x => x.ServiceFeeAmount != null) ? receipts.Sum(x => x.ServiceFeeAmount ?? 0m) : Math.Min(amount, ride.ServiceFee);
        return new Ride { Id = ride.Id, CustomerId = ride.CustomerId, DriverId = ride.DriverId, ServiceKindId = ride.ServiceKindId,
            CustomerPrice = Math.Max(0m, amount - fees), ServiceFee = fees, TotalAmount = amount };
    }

    public static async Task SynchronizeAsync(YemenDriveDbContext db, Ride ride, int actorId, CancellationToken ct)
    {
        await SnapshotEntitlementAsync(db, ride, ct);
        var money = await ReadAsync(db, ride, ct);
        if (ride.ServiceEndedAtUtc == null && ride.Status is not (RideStatus.Completed or RideStatus.Cancelled)) return;
        var reference = $"ride-receivable:{ride.Id}";
        var item = await db.RideFinancialCases.SingleOrDefaultAsync(x => x.SourceReference == reference, ct);
        if (item == null && money.OutstandingAmount <= 0) return;
        item ??= new RideFinancialCase { RideId = ride.Id, CustomerId = ride.CustomerId, DriverId = ride.DriverId, SourceReference = reference };
        if (item.Id == 0) db.RideFinancialCases.Add(item);
        item.Amount = money.OutstandingAmount;
        if (money.AmountUnderReview) item.Status = "ManualReview";
        else if (money.OutstandingAmount <= 0) item.Status = "Resolved";
        else if (item.Status is not ("Disputed" or "ManualReview")) item.Status = money.ProviderPending ? "AwaitingProvider" : "Open";
        item.Note = money.ProviderPending ? "انتهى النقل. نتحقق من محاولة المزود الأصلية قبل إثبات دين نهائي أو تحصيل بديل." : "انتهى النقل؛ المبلغ المستحق مستقل عن حالة الرحلة.";
        var desired = money.ProviderPending || money.AmountUnderReview ? 0m : money.OutstandingAmount;
        var delta = desired - item.AccruedReceivable;
        var pendingDriver = money.ProviderPending || money.AmountUnderReview ? 0m : Math.Max(0m, DriverEntitlement(ride) - (ride.CollectedDriverEntitlement ?? 0m));
        var driverDelta = pendingDriver - item.AccruedDriverEntitlement;
        var desiredFee = money.AmountDue <= 0 ? 0m : decimal.Round(Math.Min(ride.ServiceFee, money.AmountDue) * desired / money.AmountDue, 2);
        var feeDelta = desiredFee - item.AccruedServiceFee;
        if (delta != 0 || driverDelta != 0 || feeDelta != 0)
        {
            var provision = new FinancialAccountProvisioningService(db);
            await provision.ProvisionUserAsync(await db.Users.Include(x => x.Wallet).SingleAsync(x => x.Id == ride.CustomerId, ct), ct);
            if (ride.DriverId is int assignedDriver) await provision.ProvisionUserAsync(await db.Users.Include(x => x.Wallet).SingleAsync(x => x.Id == assignedDriver, ct), ct);
            await db.SaveChangesAsync(ct);
            var customerParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.User && x.EntityId == ride.CustomerId, ct);
            var account = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == customerParty.Id && x.Purpose == LedgerAccountPurpose.CustomerReceivable, ct);
            var serviceParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.ServiceKind && x.EntityId == ride.ServiceKindId, ct);
            var revenue = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == serviceParty.Id && x.Purpose == LedgerAccountPurpose.ServiceCollection, ct);
            var feeAccount = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == serviceParty.Id && x.Purpose == LedgerAccountPurpose.ServiceFee, ct);
            var lines = new List<AccountingPostingLine>();
            void Line(int accountId, decimal debit, int? partyId, int? owner = null)
            {
                if (debit != 0) lines.Add(new(accountId, Debit: Math.Max(0, debit), Credit: Math.Max(0, -debit), FinancialPartyId: partyId, UserId: owner, RideId: ride.Id));
            }
            Line(account.Id, delta, customerParty.Id, ride.CustomerId);
            Line(revenue.Id, -(delta - feeDelta), serviceParty.Id);
            Line(feeAccount.Id, -feeDelta, serviceParty.Id);
            if (driverDelta != 0 && ride.DriverId is int driverId)
            {
                var driverParty = await db.FinancialParties.SingleAsync(x => x.Type == FinancialPartyType.User && x.EntityId == driverId, ct);
                var liability = await db.LedgerAccounts.SingleAsync(x => x.FinancialPartyId == driverParty.Id && x.Purpose == LedgerAccountPurpose.DriverUncollectedEntitlement, ct);
                var expense = await db.LedgerAccounts.SingleAsync(x => x.Purpose == LedgerAccountPurpose.DriverCommission && x.IsPosting && x.IsActive, ct);
                lines.Add(new(expense.Id, Debit: driverDelta > 0 ? Math.Abs(driverDelta) : 0, Credit: driverDelta < 0 ? Math.Abs(driverDelta) : 0, UserId: driverId, RideId: ride.Id));
                lines.Add(new(liability.Id, Debit: driverDelta < 0 ? Math.Abs(driverDelta) : 0, Credit: driverDelta > 0 ? Math.Abs(driverDelta) : 0, FinancialPartyId: driverParty.Id, UserId: driverId, RideId: ride.Id));
            }
            await new AccountingPostingService(db).PostAsync(new AccountingPostingRequest(JournalEntryType.ManualAdjustment,
                $"DUE-{ride.Id}", "إثبات أو تسوية مستحق رحلة غير محصل؛ لا يمثل قبضاً نقدياً", "YER",
                lines, SourceType: "RideReceivable", SourceId: $"{reference}:{++item.JournalRevision}", CreatedByUserId: actorId), ct);
            item.AccruedReceivable = desired;
            item.AccruedServiceFee = desiredFee;
            item.AccruedDriverEntitlement = pendingDriver;
        }
        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public static async Task CloseServiceAsync(YemenDriveDbContext db, Ride ride, string reason, CancellationToken ct)
    {
        if (ride.ServiceEndedAtUtc != null) return;
        await SnapshotEntitlementAsync(db, ride, ct);
        ride.ServiceEndedAtUtc = DateTime.UtcNow;
        ride.ServiceEndReason = reason;
        ride.CustomerPaymentEnabled = false;
        ride.ArrivalAlertStoppedAtUtc ??= DateTime.UtcNow;
        var waiting = new RideWaitingCoordinator(db);
        var interval = await waiting.ReconcileAsync(ride, DateTime.UtcNow, ct);
        if (interval?.IsOpen == true) waiting.Close(ride, interval, DateTime.UtcNow, "service_ended");
        await RideRouteChangePolicy.ExpireAsync(db, ride, DateTime.UtcNow, ct);
        await RideDriverAvailability.ReleaseAsync(db, ride, ct);
    }
}
