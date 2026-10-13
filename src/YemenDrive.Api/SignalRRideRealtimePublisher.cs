using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Rides;
using YemenDrive.Database;

namespace YemenDrive.Api;

public sealed class SignalRRideRealtimePublisher(
    IHubContext<TrackingHub> hub,
    IHubContext<SharedRideHub> sharedRideHub,
    YemenDriveDbContext db,
    ILogger<SignalRRideRealtimePublisher> logger) : IRideRealtimePublisher
{
    public async Task PublishRideAvailableAsync(
        IReadOnlyCollection<RideAvailableRecipient> recipients,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            recipients,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (recipient, _) =>
            {
                try
                {
                    await hub.Clients.Group(TrackingHub.UserGroup(recipient.UserId))
                        .SendAsync("rideAvailable", recipient.Ride, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Could not publish rideAvailable for ride {RideId} to driver {DriverId}.",
                        recipient.Ride.GetValueOrDefault("id"), recipient.UserId);
                }
            });
    }

    public async Task PublishRideUnavailableAsync(
        int rideId,
        int customerUserId,
        IReadOnlyCollection<int> driverUserIds,
        CancellationToken cancellationToken)
    {
        var recipientIds = driverUserIds.Append(customerUserId).Distinct();
        await Parallel.ForEachAsync(
            recipientIds,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (driverId, _) =>
            {
                try
                {
                    await hub.Clients.Group(TrackingHub.UserGroup(driverId))
                        .SendAsync("rideUnavailable", new { rideId }, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Could not publish rideUnavailable for ride {RideId} to driver {DriverId}.",
                        rideId, driverId);
                }
            });
    }

    public async Task PublishOfferReceivedAsync(
        int customerUserId,
        RideOfferRealtimeEvent offer,
        CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.Group(TrackingHub.UserGroup(customerUserId))
                .SendAsync("offerReceived", offer, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not publish offerReceived for ride {RideId}.", offer.RideId);
        }
    }

    public async Task PublishOfferUpdatesAsync(
        int customerUserId,
        IReadOnlyCollection<RideOfferRealtimeEvent> offers,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            offers,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (offer, _) =>
            {
                try
                {
                    await hub.Clients.Group(TrackingHub.UserGroup(customerUserId))
                        .SendAsync("offerUpdated", offer, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Could not publish offerUpdated for ride {RideId} to its customer.",
                        offer.RideId);
                }

                try
                {
                    await hub.Clients.Group(TrackingHub.UserGroup(offer.DriverId))
                        .SendAsync("offerUpdated", offer, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Could not publish offerUpdated for ride {RideId} to driver {DriverId}.",
                        offer.RideId, offer.DriverId);
                }
        });
    }

    public async Task PublishRideStatusUpdatedAsync(
        RideStatusRealtimeEvent rideStatus,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            rideId = rideStatus.RideId,
            customerId = rideStatus.CustomerId,
            driverId = rideStatus.DriverId,
            rideStatus = (int)rideStatus.Status,
            statusName = rideStatus.Status.ToString(),
            rideStatus.CustomerPaymentEnabled,
            rideStatus.UpdatedAtUtc
        };
        await TrySendAsync(
            hub.Clients.Group(TrackingHub.RideGroup(rideStatus.RideId)),
            "rideStatusUpdated", payload,
            "Could not publish rideStatusUpdated for ride {RideId}.",
            rideStatus.RideId);
        await TrySendAsync(
            hub.Clients.Group(TrackingHub.OperationsGroup),
            "rideStatusUpdated", payload,
            "Could not publish rideStatusUpdated to operations for ride {RideId}.",
            rideStatus.RideId);
        await PublishToActiveShareGroupsAsync(
            rideStatus.RideId,
            "sharedRideStatusUpdated",
            new
            {
                rideId = rideStatus.RideId,
                rideStatus = (int)rideStatus.Status,
                statusName = rideStatus.Status.ToString(),
                rideStatus.UpdatedAtUtc
            },
            cancellationToken);
    }

    public async Task PublishDriverLocationAsync(
        DriverLocationRealtimeEvent location,
        CancellationToken cancellationToken)
    {
        if (location.RideId is not int rideId || location.CustomerId is null) return;
        var payload = new
        {
            rideId,
            driverId = location.DriverId,
            latitude = location.Latitude,
            longitude = location.Longitude,
            bearing = location.Bearing,
            speed = location.Speed,
            accuracy = location.Accuracy,
            observedAtUtc = location.ObservedAtUtc,
            receivedAtUtc = location.ReceivedAtUtc,
            rideStatus = location.RideStatus is null ? null : (int?)location.RideStatus.Value
        };
        await TrySendAsync(
            hub.Clients.Group(TrackingHub.RideGroup(rideId)),
            "driverLocationUpdated", payload,
            "Could not publish driverLocationUpdated for ride {RideId}.", rideId);
        await TrySendAsync(
            hub.Clients.Group(TrackingHub.OperationsGroup),
            "driverLocationUpdated", payload,
            "Could not publish driverLocationUpdated to operations for ride {RideId}.", rideId);
        await PublishToActiveShareGroupsAsync(
            rideId,
            "sharedRideLocationUpdated",
            new
            {
                rideId,
                latitude = location.Latitude,
                longitude = location.Longitude,
                bearing = location.Bearing,
                speed = location.Speed,
                observedAtUtc = location.ObservedAtUtc,
                rideStatus = location.RideStatus is null ? null : (int?)location.RideStatus.Value
            },
            cancellationToken);
    }

    public Task PublishRideMessageCreatedAsync(
        RideMessageRealtimeEvent message,
        CancellationToken cancellationToken) =>
        TrySendAsync(
            hub.Clients.Group(TrackingHub.RideGroup(message.RideId)),
            "rideMessageCreated",
            message,
            "Could not publish rideMessageCreated for ride {RideId}.",
            message.RideId);

    public Task PublishPaymentUpdatedAsync(
        IReadOnlyCollection<int> userIds,
        PaymentRealtimeEvent payment,
        CancellationToken cancellationToken) =>
        PublishToUsersAsync(
            userIds,
            "paymentUpdated",
            payment,
            payment.RideId,
            cancellationToken);

    public Task PublishCashApprovalUpdatedAsync(
        IReadOnlyCollection<int> userIds,
        PaymentRealtimeEvent approval,
        CancellationToken cancellationToken) =>
        PublishToUsersAsync(
            userIds,
            "cashApprovalUpdated",
            approval,
            approval.RideId,
            cancellationToken);

    public async Task PublishNotificationsCreatedAsync(
        IReadOnlyCollection<NotificationRealtimeEvent> notifications,
        CancellationToken cancellationToken)
    {
        var createdNotifications = notifications
            .Where(notification => notification.UserId > 0 && notification.NotificationId > 0)
            .ToArray();
        if (createdNotifications.Length == 0) return;

        // The interceptor runs after the notification transaction commits. Read
        // the persisted DataJson here so the SignalR event shares the exact
        // allowlisted routing contract used by Firebase without exposing raw JSON.
        var notificationData = new Dictionary<int, (int UserId, string? DataJson)>();
        foreach (var notificationIds in createdNotifications
                     .Select(notification => notification.NotificationId)
                     .Distinct()
                     .Chunk(1_000))
        {
            var storedNotifications = await db.Notifications.AsNoTracking()
                .Where(notification => notificationIds.Contains(notification.Id))
                .Select(notification => new
                {
                    notification.Id,
                    notification.UserId,
                    notification.DataJson
                })
                .ToListAsync(cancellationToken);
            foreach (var notification in storedNotifications)
                notificationData[notification.Id] = (notification.UserId, notification.DataJson);
        }

        await Parallel.ForEachAsync(
            createdNotifications.GroupBy(notification => notification.UserId),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (recipient, _) =>
            {
                var payload = new
                {
                    notifications = recipient.Select(notification =>
                        NotificationClientPayload.CreateSignalRNotification(
                            notification.NotificationId,
                            notification.Type,
                            notification.CreatedAtUtc,
                            notificationData.TryGetValue(notification.NotificationId, out var stored) &&
                            stored.UserId == recipient.Key
                                ? stored.DataJson
                                : null)).ToArray()
                };
                try
                {
                    await hub.Clients.Group(TrackingHub.UserGroup(recipient.Key))
                        .SendAsync("notificationCreated", payload, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Could not publish notificationCreated to user {UserId}.", recipient.Key);
                }
            });
    }

    public Task PublishSafetyIncidentCreatedAsync(
        SafetyIncidentRealtimeEvent incident,
        CancellationToken cancellationToken) =>
        TrySendAsync(
            hub.Clients.Group(TrackingHub.OperationsGroup),
            "safetyIncidentCreated",
            incident,
            "Could not publish safetyIncidentCreated for incident {IncidentId}.",
            incident.Id);

    public Task PublishSafetyIncidentUpdatedAsync(
        SafetyIncidentRealtimeEvent incident,
        CancellationToken cancellationToken) =>
        TrySendAsync(
            hub.Clients.Group(TrackingHub.OperationsGroup),
            "safetyIncidentUpdated",
            incident,
            "Could not publish safetyIncidentUpdated for incident {IncidentId}.",
            incident.Id);

    public Task PublishSafetyRecordingAvailableAsync(
        SafetyRecordingRealtimeEvent recording,
        CancellationToken cancellationToken) =>
        TrySendAsync(
            hub.Clients.Group(TrackingHub.OperationsGroup),
            "safetyRecordingAvailable",
            recording,
            "Could not publish safetyRecordingAvailable for recording {RecordingId}.",
            recording.Id);

    public Task PublishRideShareRevokedAsync(
        int shareId,
        int rideId,
        CancellationToken cancellationToken) =>
        TrySendAsync(
            sharedRideHub.Clients.Group(SharedRideHub.ShareGroup(shareId)),
            "sharedRideRevoked",
            new { rideId, revoked = true },
            "Could not publish sharedRideRevoked for share {ShareId}.",
            shareId);

    private async Task PublishToActiveShareGroupsAsync(
        int rideId,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        var shareIds = await db.RideSafetyShares.AsNoTracking()
            .Where(share => share.RideId == rideId &&
                            share.RevokedAtUtc == null &&
                            share.ExpiresAtUtc > DateTime.UtcNow)
            .Select(share => share.Id)
            .ToArrayAsync(cancellationToken);
        foreach (var shareId in shareIds)
        {
            try
            {
                await sharedRideHub.Clients.Group(SharedRideHub.ShareGroup(shareId))
                    .SendAsync(eventName, payload, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception,
                    "Could not publish {EventName} to share {ShareId} for ride {RideId}.",
                    eventName, shareId, rideId);
            }
        }
    }

    private Task PublishToUsersAsync(
        IReadOnlyCollection<int> userIds,
        string eventName,
        PaymentRealtimeEvent payload,
        int rideId,
        CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(
            userIds.Where(userId => userId > 0).Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (userId, _) =>
            {
                await TrySendAsync(
                    hub.Clients.Group(TrackingHub.UserGroup(userId)),
                    eventName,
                    payload,
                    $"Could not publish {eventName} for ride {{RideId}}.",
                    rideId);
            });

    private async Task TrySendAsync(
        IClientProxy clients,
        string eventName,
        object payload,
        string logMessage,
        int rideId)
    {
        try
        {
            await clients.SendCoreAsync(eventName, [payload], CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, logMessage, rideId);
        }
    }
}
