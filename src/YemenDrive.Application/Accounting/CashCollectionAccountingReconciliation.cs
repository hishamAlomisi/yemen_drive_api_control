using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Accounting;

// Correct the previously posted commission/share mix-up with separate immutable entries.
public sealed class CashCollectionAccountingReconciliation(YemenDriveDbContext db, AccountingPostingService posting)
{
    public async Task<int> RunAsync(CancellationToken token)
    {
        var originals = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).ThenInclude(x => x.LedgerAccount)
            .Where(x => x.Type == JournalEntryType.RideCashCollection && x.SourceType == "RideCashPayment" && x.IsPosted)
            .ToListAsync(token);
        var corrected = 0;
        foreach (var original in originals)
        {
            var expense = original.Lines.Where(x => x.LedgerAccount.Purpose == LedgerAccountPurpose.DriverCommission).ToList();
            if (expense.Count != 1 || expense[0].RideId is not int rideId) continue;
            var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rideId, token);
            if (ride?.DriverId == null) continue;
            var fare = (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) + ride.WaitingFee;
            var driverNet = ride.DriverShare ?? fare - ride.DriverCommissionAmount;
            var recorded = expense[0].Debit - expense[0].Credit;
            var difference = driverNet - recorded;
            // Only the known old calculation is repaired automatically.
            if (difference == 0 || recorded != ride.DriverCommissionAmount || driverNet < 0) continue;
            var current = original.Lines.FirstOrDefault(x => x.LedgerAccount.Purpose == LedgerAccountPurpose.DriverCurrentAccount && x.UserId == ride.DriverId);
            if (current == null) continue;
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var result = await posting.PostAsync(new AccountingPostingRequest(JournalEntryType.ManualAdjustment,
                $"CASH-SHARE-CORRECTION-{original.Id}", $"تسوية مستحق السائق للرحلة #{rideId} مع حفظ القيد الأصلي #{original.Id}", original.Currency,
                [new(expense[0].LedgerAccountId, Debit: Math.Max(0, difference), Credit: Math.Max(0, -difference),
                    UserId: ride.DriverId, RideId: rideId, Description: "تصحيح صافي مستحق السائق المثبت"),
                 new(current.LedgerAccountId, Debit: Math.Max(0, -difference), Credit: Math.Max(0, difference),
                    FinancialPartyId: current.FinancialPartyId, UserId: ride.DriverId, RideId: rideId, Description: "مطابقة ذمة السائق مع التحصيل وصافي المستحق")],
                SourceType: "CashCollectionShareCorrection", SourceId: original.Id.ToString(),
                IdempotencyKey: $"cash-share-correction:{original.Id}"), token);
            await tx.CommitAsync(token);
            if (!result.AlreadyPosted) corrected++;
        }
        return corrected;
    }
}
