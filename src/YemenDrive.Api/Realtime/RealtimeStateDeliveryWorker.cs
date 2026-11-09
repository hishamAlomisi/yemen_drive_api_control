using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Realtime;

namespace YemenDrive.Api.Realtime;

/// <summary>Durable business invalidations have their own bounded worker, never a GPS FIFO.</summary>
public sealed class RealtimeStateDeliveryWorker(IServiceScopeFactory scopes, RealtimeCommitSignal signal,
    RealtimeJournalSettings settings, DatabaseConfigurationStore configuration, IHubContext<TrackingHub> hub,
    ILogger<RealtimeStateDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled) return;
        var retention = MaintainHistoryAsync(stoppingToken);
        try {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (configuration.IsConfigured && await DeliverAsync(stoppingToken) > 0) continue;
                await signal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) {
                logger.LogError(error, "Durable state delivery will recover from its committed journal.");
                await signal.WaitAsync(stoppingToken);
            }
        }
        } finally { await retention; }
    }

    private async Task MaintainHistoryAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested) {
            var delay = TimeSpan.FromMinutes(5);
            try {
                if (configuration.IsConfigured) {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                    budget.CancelAfter(TimeSpan.FromSeconds(5));
                    var deleted = await PrunePublishedHistoryAsync(budget.Token);
                    delay = deleted == 500 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromHours(1);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "State history cleanup will retry independently of delivery."); }
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    public async Task<int> PrunePublishedHistoryAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(settings.PublishedHistoryDays, 1, 90));
        // Keep the newest invalidation for each audience. Canonical recovery uses
        // that head even after the detailed history expires. Pending events never expire.
        var ids = await db.RealtimeStateEvents.AsNoTracking().Where(old => old.PublishedAtUtc < cutoff &&
            db.RealtimeStateEvents.Any(newer => newer.StreamKey == old.StreamKey && newer.Version > old.Version &&
                newer.PublishedAtUtc != null && !old.Recipients.Any(recipient =>
                    !newer.Recipients.Any(current => current.UserId == recipient.UserId))))
            .OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).Take(500).ToArrayAsync(token);
        return ids.Length == 0 ? 0 : await db.RealtimeStateEvents.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(token);
    }

    public async Task<int> DeliverAsync(CancellationToken token)
    {
        List<Guid> candidates;
        await using (var scope = scopes.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            var now = DateTime.UtcNow;
            candidates = await db.RealtimeStateEvents
                .FromSqlRaw("SELECT * FROM [RealtimeStateEvents] WITH (READPAST,READCOMMITTEDLOCK)").AsNoTracking()
                .Where(x => x.PublishedAtUtc == null && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now) &&
                    !db.RealtimeStateEvents.Any(earlier => earlier.StreamKey == x.StreamKey && earlier.Version < x.Version && earlier.PublishedAtUtc == null))
                .OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).Take(64).ToListAsync(token);
        }
        var delivered = 0;
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (id, ct) => {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            var now = DateTime.UtcNow; var lease = Guid.NewGuid();
            var claimed = await db.RealtimeStateEvents.Where(x => x.Id == id && x.PublishedAtUtc == null &&
                    (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.LeaseId, lease)
                    .SetProperty(x => x.LeaseUntilUtc, now.AddSeconds(30)).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
            if (claimed == 0) return;
            var row = await db.RealtimeStateEvents.AsNoTracking().Include(x => x.Recipients).SingleAsync(x => x.Id == id && x.LeaseId == lease, ct);
            try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var groups = row.Recipients.Select(x => x.UserId switch {
                    0 => TrackingHub.OperationsGroup, -1 => "state:authenticated", var user => TrackingHub.UserGroup(user)
                }).Distinct().ToArray();
                await hub.Clients.Groups(groups).SendAsync("stateChanged", RealtimeStateRecovery.Envelope(row), timeout.Token);
                await db.RealtimeStateEvents.Where(x => x.Id == id && x.LeaseId == lease).ExecuteUpdateAsync(update =>
                    update.SetProperty(x => x.PublishedAtUtc, DateTime.UtcNow).SetProperty(x => x.LeaseId, (Guid?)null)
                        .SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), ct);
                Interlocked.Increment(ref delivered);
            }
            catch (Exception error) when (!ct.IsCancellationRequested) {
                logger.LogWarning(error, "State event {EventId} remains recoverable and will retry.", id);
                await db.RealtimeStateEvents.Where(x => x.Id == id && x.LeaseId == lease).ExecuteUpdateAsync(update =>
                    update.SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntilUtc, DateTime.UtcNow.AddSeconds(5)), ct);
            }
        });
        return delivered;
    }
}
