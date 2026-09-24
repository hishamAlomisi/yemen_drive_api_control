using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Safety;

public sealed record SafetyAdminReportModel(SafetyIncidentStatus? Status = null);

public sealed class SafetyAdminReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<SafetyAdminReportModel>(config)
{
    protected override async Task<object?> ListAsync(SafetyAdminReportModel model, CancellationToken token)
    {
        var admin = currentUser.RequireUserId();
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == admin && x.Role == UserRole.Admin && x.IsActive, token))
            throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");

        var query =
            from incident in db.SafetyIncidents.AsNoTracking()
            join user in db.Users.AsNoTracking() on incident.UserId equals user.Id
            join rideValue in db.Rides.AsNoTracking() on incident.RideId equals (int?)rideValue.Id into rideJoin
            from ride in rideJoin.DefaultIfEmpty()
            join driverValue in db.Users.AsNoTracking() on ride!.DriverId equals (int?)driverValue.Id into driverJoin
            from driver in driverJoin.DefaultIfEmpty()
            select new { Incident = incident, User = user, Ride = ride, Driver = driver };

        if (model.Status is not null)
            query = query.Where(x => x.Incident.Status == model.Status);

        var incidents = await query
            .OrderByDescending(x => x.Incident.CreatedAtUtc)
            .Take(300)
            .ToListAsync(token);

        var driverIds = incidents
            .Where(x => x.Ride?.DriverId is not null)
            .Select(x => x.Ride!.DriverId!.Value)
            .Distinct()
            .ToArray();
        var latestLocations = driverIds.Length == 0
            ? []
            : await db.DriverLiveLocations.AsNoTracking()
                .Where(x => driverIds.Contains(x.DriverId))
                .GroupBy(x => x.DriverId)
                .Select(group => group.OrderByDescending(x => x.ObservedAtUtc).First())
                .ToDictionaryAsync(x => x.DriverId, token);

        return incidents.Select(row =>
        {
            var driverId = row.Ride?.DriverId;
            latestLocations.TryGetValue(driverId ?? 0, out var liveLocation);
            return new
            {
                row.Incident.Id,
                row.Incident.RideId,
                userName = row.User.DisplayName,
                userPhone = row.User.PhoneNumber,
                row.Incident.Message,
                row.Incident.Latitude,
                row.Incident.Longitude,
                row.Incident.LocationObservedAtUtc,
                row.Incident.Status,
                row.Incident.AdminUserId,
                row.Incident.AdminNote,
                row.Incident.CreatedAtUtc,
                row.Incident.AcknowledgedAtUtc,
                row.Incident.ResolvedAtUtc,
                driverId,
                driverName = row.Driver?.DisplayName,
                driverPhone = row.Driver?.PhoneNumber,
                rideStatus = row.Ride?.Status,
                pickupLabel = row.Ride?.PickupLabel,
                pickupAddress = row.Ride?.PickupAddress,
                pickupLatitude = row.Ride?.PickupLatitude,
                pickupLongitude = row.Ride?.PickupLongitude,
                destinationLabel = row.Ride?.DestinationLabel,
                destinationAddress = row.Ride?.DestinationAddress,
                destinationLatitude = row.Ride?.DestinationLatitude,
                destinationLongitude = row.Ride?.DestinationLongitude,
                driverLatitude = liveLocation?.Latitude,
                driverLongitude = liveLocation?.Longitude,
                driverLocationObservedAtUtc = liveLocation?.ObservedAtUtc
            };
        }).ToList();
    }
}
