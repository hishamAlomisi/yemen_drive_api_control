using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using YemenDrive.Application.Rides;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

public sealed class FirebaseAdminPushSender(IConfiguration configuration) : IOtpTestPushSender
{
    private readonly object _gate = new();
    private FirebaseMessaging? _messaging;

    public Task<string> SendAsync(
        DevicePushInstallation installation,
        YemenDrive.Database.Entities.Notification notification,
        CancellationToken cancellationToken)
        => GetMessaging().SendAsync(CreateMessage(installation, notification), cancellationToken);

    public Task<string> SendTestOtpAsync(OtpTestPushMessage message, CancellationToken cancellationToken) =>
        GetMessaging().SendAsync(CreateTestOtpMessage(message), cancellationToken);

    public static Message CreateTestOtpMessage(OtpTestPushMessage message, DateTime? nowUtc = null)
    {
        var expires = DateTime.SpecifyKind(message.ExpiresAtUtc, DateTimeKind.Utc);
        var remaining = expires - (nowUtc ?? DateTime.UtcNow);
        var locallyRendered = message.NotificationRenderer == AndroidNotificationRendering.AppRenderer;
        var title = "رمز تحقق يمن درايف للاختبار";
        var body = $"رمز التحقق التجريبي: {message.Code}. صالح لمدة 5 دقائق.";
        var data = new Dictionary<string, string>
        {
            ["action"] = "test_otp", ["challengeId"] = message.ChallengeId,
            ["purpose"] = message.Purpose, ["clientType"] = message.ClientType,
            ["expiresAtUtc"] = expires.ToString("O")
        };
        if (locallyRendered)
        {
            data["notificationRenderer"] = AndroidNotificationRendering.AppRenderer;
            data["title"] = title;
            data["body"] = body;
        }
        return new Message
        {
            Token = message.FcmToken,
            Notification = locallyRendered ? null : new FirebaseAdmin.Messaging.Notification
            {
                Title = title, Body = body
            },
            Data = data,
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                TimeToLive = remaining <= TimeSpan.Zero ? TimeSpan.Zero
                    : remaining > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : remaining,
                Notification = locallyRendered ? null : new AndroidNotification
                {
                    ChannelId = "yemendrive_alerts", Icon = "ic_stat_yemendrive", Color = "#174C43", Sound = "default"
                }
            }
        };
    }

    public static Message CreateMessage(DevicePushInstallation installation,
        YemenDrive.Database.Entities.Notification notification)
    {
        var isRideRequest = RideRequestNotificationPolicy.RequestRideId(notification) is not null;
        var locallyRendered = installation.Platform == AndroidNotificationRendering.LocalPlatform;
        var data = NotificationClientPayload.CreateFirebaseData(notification);
        if (isRideRequest || locallyRendered)
        {
            // The app renders branded data notifications. For ride requests,
            // the driver also verifies availability before displaying the alert.
            data["title"] = Truncate(notification.Title, 100);
            data["body"] = Truncate(notification.Body, 700);
            var createdAtUtc = notification.CreatedAtUtc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(notification.CreatedAtUtc, DateTimeKind.Utc)
                : notification.CreatedAtUtc.ToUniversalTime();
            data["createdAtUtc"] = createdAtUtc.ToString("O");
        }
        if (locallyRendered) data["notificationRenderer"] = AndroidNotificationRendering.AppRenderer;
        return new Message
        {
            Token = installation.FcmToken,
            Notification = isRideRequest || locallyRendered ? null : new FirebaseAdmin.Messaging.Notification
            {
                Title = Truncate(notification.Title, 100),
                Body = Truncate(notification.Body, 700)
            },
            Data = data,
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                TimeToLive = isRideRequest ? TimeSpan.Zero : null,
                Notification = isRideRequest || locallyRendered ? null : new AndroidNotification
                {
                    ChannelId = "yemendrive_alerts",
                    Icon = "ic_stat_yemendrive",
                    Color = "#174C43",
                    Sound = "default"
                }
            }
        };

    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        var length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..length];
    }

    private FirebaseMessaging GetMessaging()
    {
        if (_messaging is not null) return _messaging;
        lock (_gate)
        {
            if (_messaging is not null) return _messaging;
            var credentialPath = configuration["GOOGLE_APPLICATION_CREDENTIALS"]
                ?? configuration["Firebase:CredentialsPath"];
            var credential = string.IsNullOrWhiteSpace(credentialPath)
                ? GoogleCredential.GetApplicationDefault()
                : CredentialFactory.FromFile<ServiceAccountCredential>(Path.IsPathRooted(credentialPath)
                    ? credentialPath
                    : Path.Combine(AppContext.BaseDirectory, credentialPath)).ToGoogleCredential();
            var firebaseApp = FirebaseApp.Create(new AppOptions
            {
                Credential = credential,
                ProjectId = configuration["Firebase:ProjectId"] ?? "yemendrive-7ab97"
            }, "YemenDrivePush");
            _messaging = FirebaseMessaging.GetMessaging(firebaseApp);
            return _messaging;
        }
    }
}
