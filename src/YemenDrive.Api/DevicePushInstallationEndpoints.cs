using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Api;

public static class DevicePushInstallationEndpoints
{
    public static IEndpointRouteBuilder MapDevicePushInstallationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/device-push/installations", async (
            RegisterDevicePushInstallation request,
            HttpContext context,
            YemenDriveDbContext db,
            ICurrentUserContext currentUser,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("DevicePushInstallation");
            var userId = currentUser.UserId;
            if (userId is null)
            {
                logger.LogWarning("FCM device registration was rejected because the request was unauthenticated.");
                return Results.Unauthorized();
            }

            var deviceId = request.DeviceId?.Trim();
            var fcmToken = request.FcmToken?.Trim();
            var platform = request.Platform?.Trim().ToLowerInvariant();
            var authenticatedDeviceId = (context.Items[AuthSessionAuthenticationMiddleware.SessionItem] as AuthenticatedSession)?.DeviceId;
            var rejectionReason = string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 128
                ? "invalid-device-id"
                : string.IsNullOrWhiteSpace(fcmToken) || fcmToken.Length > 4096
                    ? "invalid-fcm-token"
                    : platform != "android"
                        ? "unsupported-platform"
                        : string.IsNullOrWhiteSpace(authenticatedDeviceId)
                            ? "missing-authenticated-device"
                            : !string.Equals(deviceId, authenticatedDeviceId, StringComparison.Ordinal)
                                ? "device-does-not-match-session"
                                : null;
            if (rejectionReason is not null)
            {
                logger.LogWarning(
                    "FCM device registration was rejected for user {UserId}: {Reason}.",
                    userId.Value,
                    rejectionReason);
                return Results.BadRequest(ApiResult.Fail(
                    "invalid_push_installation",
                    "بيانات تسجيل الإشعارات غير صالحة."));
            }

            var normalizedDeviceId = deviceId!;
            var normalizedFcmToken = fcmToken!;
            var normalizedPlatform = platform!;
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedFcmToken)));
            var byDevice = await db.DevicePushInstallations.SingleOrDefaultAsync(
                item => item.UserId == userId && item.DeviceId == normalizedDeviceId,
                cancellationToken);
            var byToken = await db.DevicePushInstallations.SingleOrDefaultAsync(
                item => item.TokenHash == tokenHash,
                cancellationToken);

            if (byToken is not null && byDevice is not null && byToken.Id != byDevice.Id)
                db.DevicePushInstallations.Remove(byDevice);

            if (byToken is not null && (byToken.UserId != userId || byToken.DeviceId != deviceId))
            {
                await db.NotificationPushDeliveries
                    .Where(item => item.DevicePushInstallationId == byToken.Id &&
                                   item.SentAtUtc == null && item.AbandonedAtUtc == null)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            var installation = byToken ?? byDevice;
            var isNewInstallation = installation is null;
            if (installation is null)
            {
                installation = new DevicePushInstallation
                {
                    UserId = userId.Value,
                    DeviceId = normalizedDeviceId,
                    TokenHash = tokenHash,
                    FcmToken = normalizedFcmToken,
                    Platform = normalizedPlatform,
                    LastRegisteredAtUtc = DateTime.UtcNow
                };
                db.DevicePushInstallations.Add(installation);
            }
            else
            {
                installation.UserId = userId.Value;
                installation.DeviceId = normalizedDeviceId;
                installation.TokenHash = tokenHash;
                installation.FcmToken = normalizedFcmToken;
                installation.Platform = normalizedPlatform;
                installation.LastRegisteredAtUtc = DateTime.UtcNow;
                installation.UpdatedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "FCM device registration accepted for user {UserId} on {Platform} ({RegistrationMode}).",
                userId.Value,
                normalizedPlatform,
                isNewInstallation ? "created" : "updated");
            return Results.Ok(ApiResult.Ok(new { registered = true }));
        });

        endpoints.MapDelete("/api/device-push/installations/{deviceId}", async (
            string deviceId,
            HttpContext context,
            YemenDriveDbContext db,
            ICurrentUserContext currentUser,
            CancellationToken cancellationToken) =>
        {
            var userId = currentUser.UserId;
            if (userId is null) return Results.Unauthorized();
            var authenticatedDeviceId = (context.Items[AuthSessionAuthenticationMiddleware.SessionItem] as AuthenticatedSession)?.DeviceId;
            if (string.IsNullOrWhiteSpace(authenticatedDeviceId) ||
                !string.Equals(deviceId, authenticatedDeviceId, StringComparison.Ordinal))
            {
                return Results.Ok(ApiResult.Ok(new { removed = true }));
            }
            var installation = await db.DevicePushInstallations.SingleOrDefaultAsync(
                item => item.UserId == userId && item.DeviceId == authenticatedDeviceId,
                cancellationToken);
            if (installation is not null)
            {
                db.DevicePushInstallations.Remove(installation);
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(ApiResult.Ok(new { removed = true }));
        });

        return endpoints;
    }
}

public sealed record RegisterDevicePushInstallation(
    string? DeviceId,
    string? FcmToken,
    string? Platform);
