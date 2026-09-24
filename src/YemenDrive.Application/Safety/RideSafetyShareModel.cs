using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Safety;

public sealed record RideSafetyShareModel(int? Id = null, int? RideId = null, int? ExpiresInMinutes = null);

public sealed class RideSafetyShare(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext currentUser)
    : OperationsService<RideSafetyShareModel>(config)
{
    public override IReadOnlyCollection<string> Operations { get; } = ["add", "get", "cancel"];
    protected override async Task<object?> AddAsync(RideSafetyShareModel model, CancellationToken token)
    {
        var userId = currentUser.RequireUserId(); if (model.RideId is not int rideId) throw new ServiceException("ride_required", "معرف الرحلة مطلوب للمشاركة.");
        var ride = await db.Rides.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rideId, token) ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
        if (ride.CustomerId != userId && ride.DriverId != userId) throw new ServiceException("ride_access_denied", "لا تملك صلاحية مشاركة هذه الرحلة.");
        if (ride.Status is RideStatus.Completed or RideStatus.Cancelled) throw new ServiceException("ride_not_active", "لا يمكن مشاركة رحلة منتهية.");
        var minutes = Math.Clamp(model.ExpiresInMinutes ?? 120, 15, 480); var now = DateTime.UtcNow;
        var prior = await db.RideSafetyShares.Where(x => x.RideId == rideId && x.OwnerUserId == userId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now).ToListAsync(token);
        foreach (var item in prior) item.RevokedAtUtc = now;
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var itemNew = new YemenDrive.Database.Entities.RideSafetyShare { RideId = rideId, OwnerUserId = userId, TokenHash = Hash(rawToken), ExpiresAtUtc = now.AddMinutes(minutes) };
        db.RideSafetyShares.Add(itemNew); await db.SaveChangesAsync(token);
        return new { itemNew.Id, itemNew.RideId, token = rawToken, linkPath = $"/api/safety/shares/{rawToken}", itemNew.ExpiresAtUtc };
    }
    protected override async Task<object?> GetAsync(RideSafetyShareModel model, CancellationToken token)
    { var user = currentUser.RequireUserId(); var now = DateTime.UtcNow; return await db.RideSafetyShares.AsNoTracking().Where(x => x.OwnerUserId == user && x.RevokedAtUtc == null && x.ExpiresAtUtc > now).OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Id, x.RideId, x.ExpiresAtUtc }).ToListAsync(token); }
    protected override async Task<object?> CancelAsync(RideSafetyShareModel model, CancellationToken token)
    { if (model.Id is not int id) throw new ServiceException("share_id_required", "معرف رابط المشاركة مطلوب."); var item = await db.RideSafetyShares.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == currentUser.RequireUserId(), token) ?? throw new ServiceException("share_not_found", "رابط المشاركة غير موجود."); item.RevokedAtUtc ??= DateTime.UtcNow; await db.SaveChangesAsync(token); return new { item.Id, revoked = true }; }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
