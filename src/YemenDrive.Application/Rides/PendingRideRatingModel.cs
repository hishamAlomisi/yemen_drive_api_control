using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Rides;

public sealed record PendingRideRatingModel;

public sealed class PendingRideRating(YemenDriveDbContext db, DatabaseConfigurationStore config,
    ICurrentUserContext user) : OperationsService<PendingRideRatingModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["get"];

    protected override async Task<object?> GetAsync(PendingRideRatingModel model, CancellationToken token)
    {
        var customerId = user.UserId ?? throw new ServiceException("authentication_required", "سجّل الدخول لاستكمال التقييم.");
        return await Pending(db, customerId).OrderBy(x => x.CompletedAtUtc ?? x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new { rideId = x.Id, driverId = x.DriverId, driverName = x.Driver!.DisplayName })
            .FirstOrDefaultAsync(token);
    }

    internal static IQueryable<YemenDrive.Database.Entities.Ride> Pending(YemenDriveDbContext db, int customerId) =>
        db.Rides.AsNoTracking().Where(x => x.CustomerId == customerId && x.Status == RideStatus.Completed && x.DriverId != null &&
            !db.DriverRatings.Any(r => r.RideId == x.Id && r.CustomerUserId == customerId && r.Source == DriverRatingSource.Customer));

    internal static async Task EnsureCompletedAsync(YemenDriveDbContext db, int customerId, CancellationToken token)
    {
        if (await Pending(db, customerId).AnyAsync(token))
            throw new ServiceException("rating_required", "أكمل تقييم رحلتك السابقة قبل طلب رحلة جديدة.");
    }
}
