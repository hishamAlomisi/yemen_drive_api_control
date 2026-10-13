using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

/// <summary>
/// Read-only Hub access for a shared-trip capability. Each connection is
/// scoped to its exact share record so revoking one link does not affect other
/// authorized viewers of the same ride.
/// </summary>
public sealed class SharedRideHub(YemenDriveDbContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            Context.Abort();
            return;
        }

        var tokenHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
        var now = DateTime.UtcNow;
        var shareId = await (
            from share in db.RideSafetyShares.AsNoTracking()
            join ride in db.Rides.AsNoTracking() on share.RideId equals ride.Id
            where share.TokenHash == tokenHash &&
                  share.RevokedAtUtc == null &&
                  share.ExpiresAtUtc > now &&
                  ride.Status != RideStatus.Completed &&
                  ride.Status != RideStatus.Cancelled
            select (int?)share.Id)
            .SingleOrDefaultAsync(Context.ConnectionAborted);

        if (shareId is null)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            ShareGroup(shareId.Value),
            Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    internal static string ShareGroup(int shareId) => $"ride-share:{shareId}";
}
