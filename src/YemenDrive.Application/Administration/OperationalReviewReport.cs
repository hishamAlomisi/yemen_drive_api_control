using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Administration;

public sealed record OperationalReviewModel(int? RideId = null);
public sealed class OperationalReviewReport(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext user)
    : ModelService<OperationalReviewModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["list"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, OperationalReviewModel model, CancellationToken ct)
    {
        await AdministratorAccess.RequireAsync(db, user.RequireUserId(), "finance.read", ct);
        var rides = await db.Rides.AsNoTracking().Where(x => model.RideId == null || x.Id == model.RideId).ToListAsync(ct);
        var money = await RideFinancialState.ReadManyAsync(db, rides, ct);
        var ids = rides.Select(x => x.Id).ToArray();
        var cases = await db.RideFinancialCases.AsNoTracking().Where(x => ids.Contains(x.RideId)).ToListAsync(ct);
        var userNames = await db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var entries = rides.Select(x => new { x.Id, x.CustomerId, x.DriverId, x.Status, x.ServiceEndedAtUtc,
            customerName = userNames.GetValueOrDefault(x.CustomerId), driverName = x.DriverId is int driverId ? userNames.GetValueOrDefault(driverId) : null,
            x.PickupLabel, x.PickupAddress, x.DestinationLabel, x.DestinationAddress, x.CreatedAtUtc,
            financial = money[x.Id], driverEarned = RideFinancialState.DriverEntitlement(x),
            driverCollectedEntitlement = x.CollectedDriverEntitlement,
            uncollectedDriverEntitlement = cases.Where(c => c.RideId == x.Id).Sum(c => c.AccruedDriverEntitlement) }).ToList();
        return new {
            totals = new { verifiedReceipts = money.Values.Sum(x => x.PaidAmount), completedRefunds = money.Values.Sum(x => x.RefundedAmount),
                outstanding = money.Values.Where(x => !x.AmountUnderReview).Sum(x => x.OutstandingAmount),
                amountUnderReview = money.Values.Where(x => x.AmountUnderReview).Sum(x => x.AmountDue), refundEntitlement = money.Values.Sum(x => x.RefundDue),
                confirmedReceivable = cases.Where(x => x.Kind == "Receivable" && x.Status is "Open" or "Confirmed").Sum(x => x.Amount),
                disputedReceivable = cases.Where(x => x.Kind == "Receivable" && x.Status is "Disputed" or "ManualReview").Sum(x => x.Amount),
                providerUnresolved = cases.Where(x => x.Status == "AwaitingProvider").Sum(x => x.Amount),
                uncollectedDriverEntitlement = cases.Sum(x => x.AccruedDriverEntitlement),
                cashChangeOwed = cases.Where(x => x.Kind == "CashChange" && x.Status != "Resolved").Sum(x => x.Amount) },
            rides = entries.OrderByDescending(x => x.Id).Take(500),
            pendingProviderOperations = await db.PaymentWalletOperations.AsNoTracking().Where(x => x.IsOpen && (model.RideId == null || x.RideId == model.RideId))
                .OrderByDescending(x => x.Id).Take(200).Select(x => new { x.Id, x.RideId, x.CustomerId, x.Amount, x.Currency, x.ProviderCode,
                    x.RequestId, x.ProviderReference, x.Status, x.Stage, x.CreatedAtUtc, x.UpdatedAtUtc }).ToListAsync(ct),
            audit = await db.OperationalDecisionAudits.AsNoTracking().Where(x => x.Subject.Contains("Payment") || x.Subject.Contains("Refund") || x.Subject == "RideFinancialCase" || x.Subject == "RideCancellation").OrderByDescending(x => x.Id).Take(200)
                .Select(x => new { x.Id, x.ActorUserId, x.Subject, x.SubjectId, x.Action, x.Reason, x.ResultJson, x.CreatedAtUtc }).ToListAsync(ct)
        };
    }
}
