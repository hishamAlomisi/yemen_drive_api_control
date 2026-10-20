using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Rides;
using YemenDrive.Database;

namespace YemenDrive.Api;

public sealed class RideWaitingWorker(IServiceScopeFactory scopes, ILogger<RideWaitingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
                var waiting = scope.ServiceProvider.GetRequiredService<RideWaitingCoordinator>();
                var ids = await db.RideWaitingSessions.AsNoTracking().Where(x => x.IsOpen).Select(x => x.RideId)
                    .Union(db.RideRouteChanges.AsNoTracking().Where(x => x.IsOpen).Select(x => x.RideId)).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);
                    var ride = await db.Rides.SingleAsync(x => x.Id == id, stoppingToken);
                    await waiting.ReconcileAsync(ride, DateTime.UtcNow, stoppingToken);
                    await RideRouteChangePolicy.ExpireAsync(db, ride, DateTime.UtcNow, stoppingToken);
                    var changed = db.ChangeTracker.HasChanges();
                    await db.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);
                    if (changed) await waiting.PublishAsync(ride, stoppingToken);
                    db.ChangeTracker.Clear();
                }
                await db.RidePricingQuotes.Where(x => x.ExpiresAtUtc < DateTime.UtcNow.AddDays(-1)).ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Waiting reconciliation will retry.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
