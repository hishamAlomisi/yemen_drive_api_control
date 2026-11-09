using FirebaseAdmin.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using YemenDrive.Api;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Security;

var checks = 0;
var installation = new DevicePushInstallation { DeviceId = "synthetic-device", TokenHash = "synthetic-hash",
    Platform = "android", FcmToken = "synthetic-token-never-sent" };
var note = new YemenDrive.Database.Entities.Notification { Id = 42, UserId = 7,
    Type = NotificationType.Payment, Title = "تم تحديث المحفظة", Body = "رسالة تجريبية",
    DataJson = "{\"action\":\"open_wallet\",\"walletTransactionId\":123}", CreatedAtUtc = DateTime.UtcNow };
var legacy = FirebaseAdminPushSender.CreateMessage(installation, note);
Check(legacy.Notification?.Title == note.Title && legacy.Android.Notification?.Icon == "ic_stat_yemendrive",
    "older installations retain their visible FCM notification envelope");
installation.Platform = AndroidNotificationRendering.Platform("app");
var local = FirebaseAdminPushSender.CreateMessage(installation, note);
Check(local.Notification is null && local.Android.Notification is null, "local renderer avoids automatic FCM duplication");
Check(local.Data["title"] == note.Title && local.Data["body"] == note.Body && local.Data["notificationRenderer"] == "app",
    "local renderer receives display text and its explicit rendering marker");
Check(local.Data["action"] == "open_wallet" && local.Data["walletTransactionId"] == "123" && local.Data["notificationId"] == "42",
    "tap destination metadata remains intact");
Check(local.Android.Priority == Priority.High, "background notification delivery retains high priority");
Check(AndroidNotificationRendering.Platform(null) == "android" && AndroidNotificationRendering.Platform("unknown") == "android",
    "absent and unknown capabilities retain legacy delivery");
note.Type = NotificationType.RideOffer;
note.DataJson = "{\"action\":\"open_ride_offer\",\"rideId\":7}";
foreach (var platform in new[] { "android", "android-local" })
{
    installation.Platform = platform;
    var offer = FirebaseAdminPushSender.CreateMessage(installation, note);
    Check(offer.Notification is null && offer.Android.TimeToLive == TimeSpan.Zero && offer.Data["rideId"] == "7",
        "ride request remains data-only, live and verified before display");
}
var otp = new OtpTestPushMessage("synthetic-token", "synthetic-challenge", "signUp", "customer",
    DateTime.UtcNow.AddMinutes(5), "654321");
Check(FirebaseAdminPushSender.CreateTestOtpMessage(otp).Notification!.Body.Contains("654321"), "legacy OTP remains visible");
var localOtp = FirebaseAdminPushSender.CreateTestOtpMessage(otp with { NotificationRenderer = "app" });
Check(localOtp.Notification is null && localOtp.Android.Notification is null && localOtp.Data["body"].Contains("654321"),
    "new OTP is rendered with the same circular app icon");
Check(localOtp.Data["challengeId"] == otp.ChallengeId && localOtp.Data["clientType"] == "customer" &&
    localOtp.Android.TimeToLive <= TimeSpan.FromMinutes(5), "OTP app binding, challenge and expiration survive rendering changes");
note.Title = string.Concat(Enumerable.Repeat("🚕", 100));
note.Body = new string('ع', 900);
installation.Platform = "android-local";
var bounded = FirebaseAdminPushSender.CreateMessage(installation, note);
Check(bounded.Data["title"].Length <= 100 && bounded.Data["body"].Length <= 700 && !char.IsHighSurrogate(bounded.Data["title"][^1]),
    "data notification text stays bounded without splitting emoji");
await RegistrationContractsAsync();
Console.WriteLine($"PASS: {checks} notification icon transport and registration checks; no Firebase messages sent.");
void Check(bool valid, string label) { if (!valid) throw new Exception(label); checks++; }

async Task RegistrationContractsAsync()
{
    var database = "YemenDrive_NotificationIcons_" + Guid.NewGuid().ToString("N");
    var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={database};Integrated Security=True;TrustServerCertificate=True";
    var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
    var actor = new Actor();
    await using (var seed = new YemenDriveDbContext(options))
    {
        await seed.Database.MigrateAsync();
        Check(!seed.Database.HasPendingModelChanges(), "renderer capability requires no database schema changes");
        var user = new User { PhoneNumber = "770000001", DisplayName = "Synthetic", Role = UserRole.Customer,
            Wallet = new Wallet { Currency = "YER" } };
        seed.Users.Add(user); await seed.SaveChangesAsync(); actor.UserId = user.Id;
    }
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton<ICurrentUserContext>(actor);
    builder.Services.AddDbContext<YemenDriveDbContext>(o => o.UseSqlServer(connection));
    await using var app = builder.Build();
    app.Use(async (context, next) => {
        context.Items[AuthSessionAuthenticationMiddleware.SessionItem] = new AuthenticatedSession(actor.UserId!.Value,
            Guid.NewGuid(), "synthetic-device", DateTime.UtcNow.AddMinutes(5));
        await next();
    });
    app.MapDevicePushInstallationEndpoints(); await app.StartAsync();
    try
    {
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        async Task<string> Register(string? renderer) {
            using var response = await client.PostAsJsonAsync("/api/device-push/installations",
                new RegisterDevicePushInstallation("synthetic-device", "synthetic-fcm-token", "android", renderer));
            Check(response.StatusCode == HttpStatusCode.OK, "capability registration is accepted");
            await using var db = new YemenDriveDbContext(options);
            return (await db.DevicePushInstallations.SingleAsync()).Platform;
        }
        Check(await Register(null) == "android", "legacy HTTP registration retains legacy rendering");
        Check(await Register("app") == "android-local", "new HTTP registration persists app rendering capability");
        Check(await Register("unknown") == "android", "unknown renderer safely falls back to legacy rendering");
        using var invalid = await client.PostAsJsonAsync("/api/device-push/installations",
            new RegisterDevicePushInstallation("other-device", "synthetic-fcm-token", "android", "app"));
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "rendering capability cannot bypass device-session binding");
    }
    finally
    {
        await app.StopAsync();
        await using var cleanup = new YemenDriveDbContext(options);
        await cleanup.Database.EnsureDeletedAsync();
    }
}

sealed class Actor : ICurrentUserContext { public int? UserId { get; set; } }
