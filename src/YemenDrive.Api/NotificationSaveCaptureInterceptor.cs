using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

/// <summary>
/// Tracks notifications inserted during one API request. The endpoint drains this
/// after the model operation returns, so its transaction has already committed.
/// </summary>
public sealed class NotificationSaveCaptureInterceptor : SaveChangesInterceptor
{
    private readonly HashSet<Notification> _inserted = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Notification> _pushQueued = new(ReferenceEqualityComparer.Instance);

    public async Task<IReadOnlyList<NotificationRealtimeEvent>> TakeCommittedAsync(
        YemenDriveDbContext db,
        CancellationToken cancellationToken)
    {
        var candidates = _inserted
            .Where(notification => notification.Id > 0 &&
                                   db.Entry(notification).State == EntityState.Unchanged)
            .Select(notification => notification.Id)
            .Distinct()
            .ToArray();
        _inserted.Clear();
        if (candidates.Length == 0) return Array.Empty<NotificationRealtimeEvent>();

        // Confirm the rows still exist after the service's transaction completed.
        // This avoids publishing notifications from a transaction that rolled back.
        var saved = new List<NotificationRealtimeEvent>();
        foreach (var candidateBatch in candidates.Chunk(1_000))
        {
            var batch = await db.Notifications.AsNoTracking()
                .Where(notification => candidateBatch.Contains(notification.Id))
                .Select(notification => new
                {
                    notification.UserId,
                    NotificationId = notification.Id,
                    notification.Type,
                    notification.CreatedAtUtc
                })
                .ToListAsync(cancellationToken);
            saved.AddRange(batch.Select(notification => new NotificationRealtimeEvent(
                notification.UserId,
                notification.NotificationId,
                notification.Type,
                notification.CreatedAtUtc)));
        }

        return saved;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CaptureAddedNotifications(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CaptureAddedNotifications(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void CaptureAddedNotifications(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries<Notification>().ToArray())
        {
            if (entry.State != EntityState.Added) continue;

            _inserted.Add(entry.Entity);
            if (_pushQueued.Add(entry.Entity))
            {
                context.Add(new NotificationPushOutbox
                {
                    Notification = entry.Entity,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }
    }
}
