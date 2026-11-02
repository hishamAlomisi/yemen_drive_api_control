using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Application.Pricing;
using YemenDrive.Application.Payments;
using RideEntity = YemenDrive.Database.Entities.Ride;

namespace YemenDrive.Application.Rides;

public sealed record RideCancellationQuoteView(int RideId, bool PaymentCompleted, bool PaymentPending,
    bool RideStarted, decimal PaidAmount, decimal ServiceFee, decimal CancellationFee, decimal WaitingFee,
    decimal RouteChangeFees, decimal? TravelledFare, double? TravelledDistanceMeters, decimal? TotalDeductions,
    decimal? RefundAmount, string Currency, bool CanCalculateRefund, string? CalculationMessage,
    double? Latitude, double? Longitude, DateTime? ObservedAtUtc);

public static class RideCancellationQuote
{
    public static async Task<RideCancellationQuoteView> GetAsync(YemenDriveDbContext db, RideEntity ride,
        CancellationToken token, YemenDrive.Database.Entities.RideCancellationRequest? request = null,
        decimal? reviewedTravelledFare = null)
    {
        var payment = await db.PaymentTransactions.AsNoTracking()
            .Where(x => x.RideId == ride.Id && x.Status == PaymentStatus.Paid).OrderBy(x => x.Id).FirstOrDefaultAsync(token);
        var pending = await db.PaymentWalletOperations.AsNoTracking()
            .AnyAsync(x => x.RideId == ride.Id && x.IsOpen && x.Status != "Succeeded", token);
        var started = (request?.RideStatusAtRequest ?? ride.Status) == RideStatus.InProgress || ride.StartedAtUtc is not null;
        var money = await RideFinancialState.ReadAsync(db, ride, token);
        var amount = Math.Max(0m, money.PaidAmount - money.RefundedAmount);
        var currency = payment?.Currency ?? "YER";
        var serviceFee = request?.WaiveCancellationFees == true ? 0m : Math.Max(0m, ride.ServiceFee);
        var cancellationFee = request?.WaiveCancellationFees == true ? 0m : Math.Max(0m, ride.CancellationFee);
        var waitingFee = started ? Math.Max(0m, ride.WaitingFee) : 0m;
        var changeFees = started ? Math.Max(0m, ride.RouteChangeFees) : 0m;
        var fare = Math.Max(0m, (ride.CustomerPrice ?? ride.ServerPrice ?? 0m) - ride.RouteChangeFees);
        var cutoff = request?.CreatedAtUtc ?? DateTime.UtcNow;
        decimal? travelledFare = reviewedTravelledFare ?? request?.TravelledFareSnapshot ?? (started ? null : 0m);
        double? distance = 0d;
        DriverLiveLocation? location = null;
        string? message = null;
        if (started)
        {
            try
            {
                if (!ride.HasRoutePricingSnapshot) throw new ServiceException("route_pricing_unavailable", "تحتاج الإدارة إلى مراجعة أجرة الجزء المقطوع لعدم توفر تسعيرة مسار موثقة.");
                if (request?.LocationObservedAtUtc is { } observed && request.CancellationLatitude is { } latitude && request.CancellationLongitude is { } longitude)
                    location = new DriverLiveLocation { Latitude = latitude, Longitude = longitude, ObservedAtUtc = observed };
                else
                {
                    var point = await db.LocationUpdates.AsNoTracking().Where(x => x.RideId == ride.Id &&
                        x.ActorId == ride.DriverId && x.ObservedAtUtc <= cutoff.AddSeconds(5))
                        .OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
                    var live = await db.DriverLiveLocations.AsNoTracking().SingleOrDefaultAsync(x => x.DriverId == ride.DriverId, token);
                    location = live is not null && live.ObservedAtUtc <= cutoff.AddSeconds(5) &&
                        (point is null || live.ObservedAtUtc >= point.ObservedAtUtc) ? live :
                        point is null ? null : new DriverLiveLocation { Latitude = point.Latitude, Longitude = point.Longitude,
                            ObservedAtUtc = point.ObservedAtUtc, Speed = point.Speed };
                }
                if (location is null) throw new ServiceException("route_track_unavailable", "تحتاج الإدارة إلى مراجعة المسافة المقطوعة لعدم توفر سجل موقع موثوق.");
                if ((cutoff - location.ObservedAtUtc).TotalSeconds > 120)
                    throw new ServiceException("route_track_stale", "تحتاج الإدارة إلى مراجعة أجرة الجزء المقطوع لأن آخر موقع موثوق لا يغطي وقت طلب الإلغاء.");
                var travelled = await RideRouteChangePolicy.MeasureAsync(db, ride, location, token);
                var rawOriginal = ride.RouteBaseFare + ride.RoutePerKilometer * (decimal)(ride.RouteQuotedDistanceMeters / 1000d) +
                    ride.RoutePerMinute * (decimal)(ride.RouteQuotedDurationSeconds / 60d);
                if (rawOriginal <= 0 || ride.RouteOriginalFare <= 0) throw new ServiceException("route_pricing_unavailable", "تحتاج الإدارة إلى مراجعة تسعيرة الجزء المقطوع.");
                var factor = ride.RouteOriginalFare / rawOriginal;
                travelledFare ??= ride.RouteCompletedFare + RidePricingPolicy.Round(((ride.RouteVersion == 0 ? ride.RouteBaseFare : 0m) +
                    ride.RoutePerKilometer * (decimal)(travelled.Meters / 1000d) + ride.RoutePerMinute * (decimal)(travelled.Seconds / 60d)) * factor);
                distance = ride.RouteCompletedDistanceMeters + travelled.Meters;
            }
            catch (ServiceException error) { distance = null; message = error.Message; }
        }
        if (reviewedTravelledFare is not null)
        {
            if (reviewedTravelledFare < 0 || reviewedTravelledFare > fare)
                throw new ServiceException("invalid_travelled_fare", "أجرة الجزء المقطوع يجب أن تكون بين الصفر وأجرة الرحلة الأصلية.");
            travelledFare = reviewedTravelledFare;
        }
        var fixedFees = serviceFee + cancellationFee + waitingFee + changeFees;
        decimal? refund = travelledFare is null ? null :
            Math.Max(0m, amount - fixedFees - Math.Min(fare, travelledFare.Value));
        return new RideCancellationQuoteView(ride.Id, money.PaymentCompleted, pending, started, amount,
            serviceFee, cancellationFee, waitingFee, changeFees, travelledFare, distance,
            travelledFare is null ? null : fixedFees + Math.Min(fare, travelledFare.Value), refund, currency, refund is not null, message,
            location?.Latitude, location?.Longitude, location?.ObservedAtUtc);
    }
}
