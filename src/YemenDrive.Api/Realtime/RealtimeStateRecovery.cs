using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Database.Realtime;

namespace YemenDrive.Api.Realtime;

public sealed record StateChangeEnvelope(Guid EventId, string Stream, long Version, string Resource, int ResourceId,
    string Changes, DateTime CreatedAtUtc, int SchemaVersion = 1, Guid OperationId = default);
public sealed record StateReplayPage(bool Supported, IReadOnlyList<StateChangeEnvelope> Events, bool HasMore, string? NextStream = null);

public sealed class RealtimeStateRecovery(YemenDriveDbContext db, RealtimeJournalSettings settings)
{
    public async Task<StateReplayPage> ReadAsync(int userId, Dictionary<string, long>? versions, CancellationToken token, string? afterStream = null)
    {
        if (!settings.Enabled) return new(false, [], false);
        if (versions is { Count: > 512 } || versions?.Any(x => x.Key.Length > 120 || x.Value < 0) == true || afterStream?.Length > 120)
            throw new HubException("نطاق استعادة الأحداث غير صالح.");
        var role = await db.Users.AsNoTracking().Where(x => x.Id == userId && x.IsActive).Select(x => (UserRole?)x.Role).SingleOrDefaultAsync(token);
        if (role == null) throw new HubException("انتهت صلاحية متابعة الحساب.");
        var audience = role == UserRole.Admin ? new[] { userId, 0, -1 } : new[] { userId, -1 };
        var query = db.RealtimeStateEvents.AsNoTracking().Where(x => x.Recipients.Any(r => audience.Contains(r.UserId)));
        // Recovery loads canonical resource snapshots, so one invalidation at the
        // highest committed authorized version covers all changes to that resource.
        // The keyset cursor bounds replay even after more than 512 historical rides.
        query = query.Where(x => !db.RealtimeStateEvents.Any(newer => newer.StreamKey == x.StreamKey &&
            newer.Version > x.Version && newer.Recipients.Any(r => audience.Contains(r.UserId))));
        if (!string.IsNullOrEmpty(afterStream)) query = query.Where(x => string.Compare(x.StreamKey, afterStream) > 0);
        // Compare per-resource versions. A global identity/cursor could skip a later
        // committed transaction that allocated its identity before another transaction.
        foreach (var known in versions ?? []) {
            var key = known.Key; var version = known.Value;
            query = query.Where(x => x.StreamKey != key || x.Version > version);
        }
        var rows = await query.OrderBy(x => x.StreamKey).ThenBy(x => x.Version).Take(129).ToListAsync(token);
        var events = rows.Take(128).Select(x => Envelope(x) with { Changes = "*" }).ToArray();
        return new(true, events, rows.Count > 128, rows.Count > 128 ? events[^1].Stream : null);
    }

    public static StateChangeEnvelope Envelope(RealtimeStateEvent row) => new(row.Id, row.StreamKey, row.Version,
        row.Resource, row.ResourceId, row.Changes, DateTime.SpecifyKind(row.CreatedAtUtc, DateTimeKind.Utc), OperationId: row.OperationId);
}
