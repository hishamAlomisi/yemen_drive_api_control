using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
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
    {
        var message = new Message
        {
            Token = installation.FcmToken,
            Notification = new FirebaseAdmin.Messaging.Notification
            {
                Title = Truncate(notification.Title, 100),
                Body = Truncate(notification.Body, 700)
            },
            Data = NotificationClientPayload.CreateFirebaseData(notification),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = "yemendrive_alerts",
                    Icon = "ic_stat_yemendrive",
                    Color = "#0F6B78",
                    Sound = "default"
                }
            }
        };

        return GetMessaging().SendAsync(message, cancellationToken);
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
            var firebaseApp = FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.GetApplicationDefault(),
                ProjectId = configuration["Firebase:ProjectId"] ?? "yemendrive-7ab97"
            }, "YemenDrivePush");
            _messaging = FirebaseMessaging.GetMessaging(firebaseApp);
            return _messaging;
        }
    }
}
