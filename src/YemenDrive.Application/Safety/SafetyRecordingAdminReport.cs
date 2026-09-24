using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Safety;

public sealed record SafetyRecordingAdminReportModel();

public sealed class SafetyRecordingAdminReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<SafetyRecordingAdminReportModel>(config)
{
    protected override async Task<object?> ListAsync(SafetyRecordingAdminReportModel model, CancellationToken token)
    {
        var adminId = currentUser.RequireUserId();
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == adminId && x.Role == UserRole.Admin && x.IsActive, token))
            throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");

        var recordings = await (
            from recording in db.EmergencyRecordings.AsNoTracking()
            join owner in db.Users.AsNoTracking() on recording.UserId equals owner.Id
            join rideValue in db.Rides.AsNoTracking() on recording.RideId equals (int?)rideValue.Id into rideJoin
            from ride in rideJoin.DefaultIfEmpty()
            where recording.EndedAtUtc != null && recording.UploadedBytes > 0
            orderby recording.StartedAtUtc descending
            select new { Recording = recording, Owner = owner, Ride = ride })
            .Take(300)
            .ToListAsync(token);

        var driverIds = recordings.Where(x => x.Ride?.DriverId is not null)
            .Select(x => x.Ride!.DriverId!.Value).Distinct().ToArray();
        var drivers = driverIds.Length == 0
            ? new Dictionary<int, User>()
            : await db.Users.AsNoTracking().Where(x => driverIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, token);

        return recordings.Select(row =>
        {
            var driverId = row.Ride?.DriverId;
            drivers.TryGetValue(driverId ?? 0, out var driver);
            return new
            {
                row.Recording.Id,
                row.Recording.RideId,
                userId = row.Owner.Id,
                userName = row.Owner.DisplayName,
                userPhone = row.Owner.PhoneNumber,
                userRole = row.Owner.Role,
                driverId,
                driverName = driver?.DisplayName,
                driverPhone = driver?.PhoneNumber,
                rideStatus = row.Ride?.Status,
                pickupLabel = row.Ride?.PickupLabel,
                destinationLabel = row.Ride?.DestinationLabel,
                row.Recording.UploadedBytes,
                row.Recording.DurationMilliseconds,
                row.Recording.ContentHash,
                row.Recording.ConsentAtUtc,
                row.Recording.StartedAtUtc,
                row.Recording.LastChunkAtUtc,
                row.Recording.EndedAtUtc
            };
        }).ToList();
    }
}
