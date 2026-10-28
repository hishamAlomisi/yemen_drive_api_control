using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using YemenDrive.Application.Rides;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

public sealed class FirebaseAdminPushSender(IConfiguration configuration)
{
    private readonly object _gate = new();
    private FirebaseMessaging? _messaging;

    public Task<string> SendAsync(
        DevicePushInstallation installation,
        YemenDrive.Database.Entities.Notification notification,
        CancellationToken cancellationToken)
        => GetMessaging().SendAsync(CreateMessage(installation, notification), cancellationToken);

    public static Message CreateMessage(DevicePushInstallation installation,
        YemenDrive.Database.Entities.Notification notification)
    {
        var isRideRequest = RideRequestNotificationPolicy.RequestRideId(notification) is not null;
        var data = NotificationClientPayload.CreateFirebaseData(notification);
        if (isRideRequest)
        {
            // The driver app verifies availability before displaying data messages.
            // Android must not display an obsolete request directly from its tray.
            data["title"] = Truncate(notification.Title, 100);
            data["body"] = Truncate(notification.Body, 700);
            data["createdAtUtc"] = notification.CreatedAtUtc.ToUniversalTime().ToString("O");
        }
        return new Message
        {
            Token = installation.FcmToken,
            Notification = isRideRequest ? null : new FirebaseAdmin.Messaging.Notification
            {
                Title = Truncate(notification.Title, 100),
                Body = Truncate(notification.Body, 700)
            },
            Data = data,
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                TimeToLive = isRideRequest ? TimeSpan.Zero : null,
                Notification = isRideRequest ? null : new AndroidNotification
                {
                    ChannelId = "yemendrive_alerts",
                    Icon = "ic_launcher",
                    Color = "#0F6B78",
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
