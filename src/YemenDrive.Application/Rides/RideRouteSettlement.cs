using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Payments;
using YemenDrive.Application.Pricing;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public static class RideRouteSettlement
{
    public static Task<RideRouteChange?> PendingAsync(YemenDriveDbContext db, RideEntity ride, CancellationToken ct) =>
        db.RideRouteChanges.SingleOrDefaultAsync(x => x.RideId == ride.Id && x.Status == "awaiting_payment" && x.IsOpen && ride.ServiceEndedAtUtc == null, ct);

    public static decimal ProjectedDriverEntitlement(RideEntity ride, RideRouteChange request)
    {
        var fare = request.NewTotal - request.OriginalServiceFee;
        return Math.Max(0, fare - RidePricingPolicy.Round(fare * ride.AppliedDriverCommissionRate + ride.AppliedDriverCommissionFixed));
    }
    public static async Task ApplyAsync(YemenDriveDbContext db, RideEntity ride, RideRouteChange request, CancellationToken ct)
    {
        if (ride.ServiceEndedAtUtc != null || ride.RouteVersion != request.RouteVersion) return;
        await RideFinancialState.SnapshotEntitlementAsync(db, ride, ct);
        request.IsOpen = false; request.Status = "accepted"; request.DecidedAtUtc = DateTime.UtcNow; request.UpdatedAtUtc = DateTime.UtcNow;
        ride.DestinationLatitude = request.NewDestinationLatitude; ride.DestinationLongitude = request.NewDestinationLongitude;
        ride.DestinationLabel = request.NewDestinationLabel; ride.DestinationAddress = request.NewDestinationLabel;
        ride.RoutePolyline = request.NewRoutePolyline; ride.RouteCompletedFare = request.CompletedFare;
        ride.RouteCompletedDistanceMeters = request.CompletedDistanceMeters;
        ride.RouteChangeFees = request.PreviousRouteChangeFees + request.RouteChangeFee;
        ride.RouteSegmentStartedAtUtc = request.ChangeObservedAtUtc; ride.RouteSegmentLatitude = request.ChangeLatitude; ride.RouteSegmentLongitude = request.ChangeLongitude;
        ride.RouteVersion++; ride.CustomerPrice = request.CompletedFare + request.NewRouteFare + ride.RouteChangeFees; ride.ServerPrice = ride.CustomerPrice;
        RidePricingPolicy.UpdateTotals(ride); ride.CustomerPaymentEnabled = false; ride.UpdatedAtUtc = DateTime.UtcNow;
        foreach (var recipient in new[] { ride.CustomerId, ride.DriverId ?? ride.CustomerId }.Distinct()) db.Notifications.Add(new() { UserId = recipient, Type = NotificationType.RideStatus, Title = "تم اعتماد المسار الجديد", Body = $"الوجهة: {request.NewDestinationLabel}. الإجمالي {ride.TotalAmount:0.##} ر.ي.", DataJson = $"{{\"action\":\"open_active_ride\",\"rideId\":{ride.Id}}}" });
        await db.SaveChangesAsync(ct);
        var money = await RideFinancialState.ReadAsync(db, ride, ct);
        ride.CustomerPaymentEnabled = money.OutstandingAmount > 0;
        foreach (var previous in await db.RideFinancialCases.Where(x => x.RideId == ride.Id && x.Kind == "FareDecrease" && (x.Status == "AwaitingConsent" || x.Status == "AwaitingCashReturn")).ToListAsync(ct))
        {
            previous.Status = "Superseded";
            previous.Note = "أعيد احتساب فرق الأجرة بعد اعتماد تغيير جديد؛ راجع الاستحقاق الحالي.";
            previous.UpdatedAtUtc = DateTime.UtcNow;
        }
        if (money.RefundDue > 0)
        {
            var reference = $"route-decrease:{request.Id}";
            if (!await db.RideFinancialCases.AnyAsync(x => x.SourceReference == reference, ct)) db.RideFinancialCases.Add(new() { RideId = ride.Id, CustomerId = ride.CustomerId, DriverId = ride.DriverId, Kind = "FareDecrease", Status = "AwaitingConsent", Amount = money.RefundDue, SourceReference = reference, Note = "فرق خفض الأجرة حق للراكب. استرداد المصدر الخارجي غير موثق؛ يمكن قبول محفظة التطبيق أو طلب معالجة مالية بديلة." });
        }
    }
    public static async Task CompleteApprovedAsync(YemenDriveDbContext db, RideEntity ride, CancellationToken ct)
    {
        var pending = await PendingAsync(db, ride, ct);
        if (pending == null) return;
        var net = await db.PaymentTransactions.Where(x => x.RideId == ride.Id && (x.Status == PaymentStatus.Paid || x.Status == PaymentStatus.Refunded)).SumAsync(x => x.Status == PaymentStatus.Paid ? x.Amount : -x.Amount, ct);
        if (net >= pending.NewTotal) await ApplyAsync(db, ride, pending, ct);
    }
}
