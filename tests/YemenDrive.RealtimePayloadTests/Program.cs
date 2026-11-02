using System.Globalization;
using YemenDrive.Api;
using YemenDrive.Database.Entities;

var expected = new DateTime(2026, 11, 2, 18, 15, 20, DateTimeKind.Utc);
foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc })
{
    var message = FirebaseAdminPushSender.CreateMessage(
        new DevicePushInstallation { DeviceId = "test-device", TokenHash = "test-hash", Platform = "android", FcmToken = "test-token-not-sent" },
        new Notification {
            Id = 11, UserId = 42, Type = NotificationType.RideOffer,
            Title = "طلب رحلة", Body = "اختبار", CreatedAtUtc = DateTime.SpecifyKind(expected, kind),
            DataJson = "{\"action\":\"open_ride_offer\",\"rideId\":7}"
        });
    var timestamp = message.Data["createdAtUtc"];
    var parsed = DateTime.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    if (!timestamp.EndsWith('Z') || parsed.Kind != DateTimeKind.Utc || parsed != expected)
        throw new Exception("Stored notification UTC time was shifted by the server timezone.");
    if (message.Notification is not null || message.Data["rideId"] != "7")
        throw new Exception("A request must remain a verifiable data message.");
}
Console.WriteLine("PASS: database and UTC notification timestamps retain the same instant in FCM payloads; no push sent.");
