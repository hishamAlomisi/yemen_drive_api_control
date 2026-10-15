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
                    Icon = "ic_launcher",
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
