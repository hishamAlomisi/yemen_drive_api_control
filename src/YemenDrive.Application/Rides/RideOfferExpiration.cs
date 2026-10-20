using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Rides;

public sealed class RideOfferExpiration(YemenDriveDbContext db, IRideRealtimePublisher publisher)
{
    public async Task<int> ExpireAsync(CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var now = DateTime.UtcNow;
        var expired = await db.RideOffers.Include(x => x.Ride)
            .Where(x => x.Status == OfferStatus.Pending && x.ExpiresAtUtc <= now)
            .OrderBy(x => x.ExpiresAtUtc).ThenBy(x => x.Id).Take(200).ToListAsync(token);
        foreach (var offer in expired)
        {
            offer.Status = OfferStatus.Expired;
            offer.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        foreach (var group in expired.GroupBy(x => x.Ride.CustomerId))
        {
            var events = await RideRealtimeEventFactory.LoadOffersAsync(db, group.Select(x => x.Id), token);
            await publisher.PublishOfferUpdatesAsync(group.Key, events, token);
        }
        return expired.Count;
    }
}
