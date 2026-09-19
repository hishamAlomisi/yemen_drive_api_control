using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

/// <summary>
/// Read-only administrative queue. Financial reversals are deliberately not
/// performed here: an administrator first sees the complete ride and decision
/// history before choosing the cancellation outcome.
/// </summary>
public sealed class RideCancellationReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<RideCancellationReportModel>(config)
{
    protected override async Task<object?> ListAsync(RideCancellationReportModel model, CancellationToken token)
    {
        var adminId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كمدير.");
        var isAdmin = await db.Users.AsNoTracking().AnyAsync(x => x.Id == adminId && x.Role == UserRole.Admin && x.IsActive, token);
        if (!isAdmin) throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");

        var query = db.RideCancellationRequests.AsNoTracking()
            .Include(x => x.Ride).ThenInclude(x => x.Customer)
            .Include(x => x.Ride).ThenInclude(x => x.Driver)
            .AsQueryable();
        if (model.Status is not null) query = query.Where(x => x.Status == model.Status);
        if (model.RideId is not null) query = query.Where(x => x.RideId == model.RideId);

        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Take(300).ToListAsync(token);
        var rideIds = rows.Select(x => x.RideId).Distinct().ToArray();
        var payments = await db.PaymentTransactions.AsNoTracking()
            .Where(x => x.RideId != null && rideIds.Contains(x.RideId.Value))
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(token);

        return rows.Select(x =>
        {
            var payment = payments.FirstOrDefault(p => p.RideId == x.RideId);
            return new
            {
                x.Id, x.RideId, x.Status, x.DriverDecision, x.RideStatusAtRequest,
                customerName = x.Ride.Customer.DisplayName,
                customerPhone = x.Ride.Customer.PhoneNumber,
                driverName = x.Ride.Driver?.DisplayName,
                driverPhone = x.Ride.Driver?.PhoneNumber,
                pickup = RideLocationText.DisplayName(x.Ride.PickupLabel, x.Ride.PickupAddress, "نقطة الانطلاق"),
                destination = RideLocationText.DisplayName(x.Ride.DestinationLabel, x.Ride.DestinationAddress, "الوجهة"),
                x.Ride.CustomerPrice, x.Ride.ServiceFee, x.Ride.TotalAmount,
                x.Reason, x.RequestedRefundMethod, x.RequestedRefundAmount,
                x.CancellationLatitude, x.CancellationLongitude, x.LocationObservedAtUtc,
                x.DriverDecidedAtUtc, x.DriverNote, x.AdminDecidedAtUtc, x.AdminNote, x.CreatedAtUtc,
                paymentProvider = payment?.Provider,
                paymentStatus = payment?.Status,
                paymentAmount = payment?.Amount
            };
        }).ToList();
    }
}
