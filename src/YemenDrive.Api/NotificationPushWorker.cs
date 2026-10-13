using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;

namespace YemenDrive.Api;

public sealed class NotificationPushWorker(
    IServiceScopeFactory scopeFactory,
    FirebaseAdminPushSender sender,
    DatabaseConfigurationStore databaseConfiguration,
    ILogger<NotificationPushWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private const int MaxAttempts = 12;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!databaseConfiguration.IsConfigured)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            try
            {
                await CaptureRecipientsAsync(stoppingToken);
                await SendDueDeliveriesAsync(stoppingToken);
                await CompleteOutboxesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Notification push worker iteration failed ({ErrorType}).", exception.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CaptureRecipientsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var now = DateTime.UtcNow;
        var candidates = await db.NotificationPushOutbox.AsNoTracking()
            .Where(item => item.RecipientsCapturedAtUtc == null &&
                           (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
            .OrderBy(item => item.CreatedAtUtc)
            .Select(item => item.Id)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var id in candidates)
        {
            var leaseId = Guid.NewGuid();
            var claimed = await db.NotificationPushOutbox
                .Where(item => item.Id == id && item.RecipientsCapturedAtUtc == null &&
                               (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LeaseId, leaseId)
                    .SetProperty(item => item.LeaseUntilUtc, now.Add(LeaseDuration)), cancellationToken);
            if (claimed == 0) continue;

            var outbox = await db.NotificationPushOutbox
                .Include(item => item.Notification)
                .SingleAsync(item => item.Id == id && item.LeaseId == leaseId, cancellationToken);
            var installationIds = await db.DevicePushInstallations.AsNoTracking()
                .Where(item => item.UserId == outbox.Notification.UserId &&
                               db.AuthSessions.Any(session =>
                                   session.UserId == item.UserId &&
                                   session.DeviceId == item.DeviceId &&
                                   session.RevokedAtUtc == null &&
                                   session.SessionExpiresAtUtc > now))
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);

            foreach (var installationId in installationIds)
            {
                db.NotificationPushDeliveries.Add(new YemenDrive.Database.Entities.NotificationPushDelivery
                {
                    OutboxId = outbox.Id,
                    DevicePushInstallationId = installationId,
                    NextAttemptAtUtc = now,
                    CreatedAtUtc = now
                });
            }

            outbox.RecipientsCapturedAtUtc = now;
            if (installationIds.Count == 0) outbox.CompletedAtUtc = now;
            outbox.LeaseId = null;
            outbox.LeaseUntilUtc = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SendDueDeliveriesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var now = DateTime.UtcNow;
        var candidates = await db.NotificationPushDeliveries.AsNoTracking()
            .Where(item => item.SentAtUtc == null && item.AbandonedAtUtc == null &&
                           item.NextAttemptAtUtc <= now &&
                           (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
            .OrderBy(item => item.NextAttemptAtUtc)
            .Select(item => item.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var id in candidates)
        {
            var leaseId = Guid.NewGuid();
            var claimed = await db.NotificationPushDeliveries
                .Where(item => item.Id == id && item.SentAtUtc == null && item.AbandonedAtUtc == null &&
                               item.NextAttemptAtUtc <= now &&
                               (item.LeaseUntilUtc == null || item.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LeaseId, leaseId)
                    .SetProperty(item => item.LeaseUntilUtc, now.Add(LeaseDuration))
                    .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1), cancellationToken);
            if (claimed == 0) continue;

            var delivery = await db.NotificationPushDeliveries.AsNoTracking()
                .Include(item => item.Installation)
                .Include(item => item.Outbox)
                    .ThenInclude(item => item.Notification)
                .SingleOrDefaultAsync(item => item.Id == id && item.LeaseId == leaseId, cancellationToken);
            if (delivery is null) continue;

            var sessionIsActive = await db.AuthSessions.AsNoTracking().AnyAsync(session =>
                session.UserId == delivery.Installation.UserId &&
                session.DeviceId == delivery.Installation.DeviceId &&
                session.RevokedAtUtc == null &&
                session.SessionExpiresAtUtc > DateTime.UtcNow,
                cancellationToken);
            if (!sessionIsActive)
            {
                await db.DevicePushInstallations
                    .Where(item => item.Id == delivery.DevicePushInstallationId)
                    .ExecuteDeleteAsync(cancellationToken);
                continue;
            }

            try
            {
                await sender.SendAsync(delivery.Installation, delivery.Outbox.Notification, cancellationToken);
                await db.NotificationPushDeliveries
                    .Where(item => item.Id == id && item.LeaseId == leaseId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.SentAtUtc, DateTime.UtcNow)
                        .SetProperty(item => item.LeaseId, (Guid?)null)
                        .SetProperty(item => item.LeaseUntilUtc, (DateTime?)null)
                        .SetProperty(item => item.LastErrorCode, (string?)null), cancellationToken);
            }
            catch (FirebaseMessagingException exception) when (exception.MessagingErrorCode == MessagingErrorCode.Unregistered)
            {
                await db.DevicePushInstallations
                    .Where(item => item.Id == delivery.DevicePushInstallationId)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            catch (FirebaseMessagingException exception) when (
                exception.MessagingErrorCode is MessagingErrorCode.InvalidArgument or MessagingErrorCode.SenderIdMismatch)
            {
                await AbandonAsync(
                    db,
                    id,
                    leaseId,
                    exception.MessagingErrorCode?.ToString() ?? "invalid_argument",
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (FirebaseMessagingException exception)
            {
                await RetryOrAbandonAsync(db, delivery, leaseId, exception.MessagingErrorCode?.ToString() ?? "fcm_error", cancellationToken);
            }
            catch (Exception exception)
            {
                await RetryOrAbandonAsync(db, delivery, leaseId, exception.GetType().Name, cancellationToken);
            }
        }
    }

    private static async Task AbandonAsync(
        YemenDriveDbContext db,
        int deliveryId,
        Guid leaseId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await db.NotificationPushDeliveries
            .Where(item => item.Id == deliveryId && item.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AbandonedAtUtc, DateTime.UtcNow)
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(item => item.LastErrorCode, errorCode), cancellationToken);
    }

    private static async Task RetryOrAbandonAsync(
        YemenDriveDbContext db,
        YemenDrive.Database.Entities.NotificationPushDelivery delivery,
        Guid leaseId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var attempts = delivery.AttemptCount;
        var abandon = attempts >= MaxAttempts;
        var delaySeconds = Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts - 1, 9)));
        await db.NotificationPushDeliveries
            .Where(item => item.Id == delivery.Id && item.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AbandonedAtUtc, abandon ? now : (DateTime?)null)
                .SetProperty(item => item.NextAttemptAtUtc, now.AddSeconds(delaySeconds))
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(item => item.LastErrorCode, errorCode), cancellationToken);
    }

    private async Task CompleteOutboxesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var completedIds = await db.NotificationPushOutbox.AsNoTracking()
            .Where(outbox => outbox.RecipientsCapturedAtUtc != null && outbox.CompletedAtUtc == null &&
                             !outbox.Deliveries.Any(delivery => delivery.SentAtUtc == null && delivery.AbandonedAtUtc == null))
            .OrderBy(outbox => outbox.Id)
            .Select(outbox => outbox.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (completedIds.Count == 0) return;

        var now = DateTime.UtcNow;
        await db.NotificationPushOutbox
            .Where(outbox => completedIds.Contains(outbox.Id) && outbox.CompletedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(outbox => outbox.CompletedAtUtc, now), cancellationToken);
    }
}
