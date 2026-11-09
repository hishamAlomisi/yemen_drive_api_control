using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FirebaseAdmin.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YemenDrive.Api;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Providers;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Security;

const string pushToken = "synthetic-fcm-token-never-sent-to-firebase";
const string deviceId = "synthetic-otp-device-01";
var checks = 0;
await DeliveryAndChallengeGuardsAsync();
await HttpAuthenticationContractsAsync();
Console.WriteLine($"PASS: {checks} OTP delivery checks; mock transport only and disposable LocalDB.");
if (args.Contains("--firebase-dry-run")) await FirebaseDryRunAsync();

async Task DeliveryAndChallengeGuardsAsync()
{
    var originalOutput = Console.Out;
    using var captured = new StringWriter();
    try
    {
        Console.SetOut(captured);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var sender = new MockPush();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new OtpChallengeStore(consoleOtpEnabled: false, clock);
        var delivery = new OtpDeliveryService(store, sender, configuration);
        Check(!delivery.TestPushEnabled, "test delivery defaults to disabled");
        var disabled = await delivery.CreateAsync("967700889601", "signIn:customer", pushToken, deviceId, "customer", default);
        Check(!disabled.Success && disabled.ErrorCode == "otp_delivery_unavailable" && sender.Messages.Count == 0,
            "production default does not generate or push OTP");
        configuration["Otp:EnableTestPushDelivery"] = "true";
        Check(delivery.TestPushEnabled, "runtime configuration enables delivery dynamically");
        foreach (var item in new[]
        {
            (Token: (string?)null, Device: deviceId, Client: "customer", Code: "otp_push_token_required"),
            (Token: "short", Device: deviceId, Client: "customer", Code: "invalid_otp_push_token"),
            (Token: pushToken, Device: "bad", Client: "customer", Code: "invalid_device"),
            (Token: pushToken, Device: deviceId, Client: "admin", Code: "invalid_client_type")
        })
        {
            var rejected = await delivery.CreateAsync("967700889601", "signUp", item.Token, item.Device, item.Client, default);
            Check(!rejected.Success && rejected.ErrorCode == item.Code, "missing or invalid push target is a clear refusal");
        }
        Check(sender.Messages.Count == 0, "invalid push targets never invoke transport");
        var issued = await delivery.CreateAsync("967700889601", "signIn:customer", pushToken, deviceId, "customer", default);
        var message = sender.Messages.Last();
        Check(issued.Success && issued.ChallengeId == message.ChallengeId && issued.ExpiresInSeconds == 300,
            "challenge is returned after ephemeral push acceptance");
        Check(Regex.IsMatch(message.Code, "^[0-9]{6}$") && message.FcmToken == pushToken && message.ClientType == "customer",
            "six-digit OTP is delivered to the requesting app token");
        var envelope = JsonSerializer.Serialize(issued);
        Check(!envelope.Contains(message.Code) && !envelope.Contains(pushToken), "issue response never contains the code or device token");
        var firebase = FirebaseAdminPushSender.CreateTestOtpMessage(message, clock.GetUtcNow().UtcDateTime);
        Check(firebase.Token == pushToken && firebase.Notification.Body.Contains(message.Code) &&
            firebase.Android.Priority == Priority.High && firebase.Android.Notification.ChannelId == "yemendrive_alerts",
            "real Firebase message shape uses existing channel, high priority and visible Arabic code");
        Check(firebase.Android.TimeToLive > TimeSpan.Zero && firebase.Android.TimeToLive <= TimeSpan.FromMinutes(5),
            "test OTP push TTL is capped at five minutes");
        Check(firebase.Data["action"] == "test_otp" && firebase.Data["challengeId"] == issued.ChallengeId &&
            firebase.Data["purpose"] == "signIn:customer" && firebase.Data["clientType"] == "customer" &&
            firebase.Data["expiresAtUtc"].EndsWith('Z') && !firebase.Data.Values.Contains(message.Code),
            "ephemeral metadata carries challenge, purpose, client and UTC expiry without a raw code field");
        Check(store.Verify(issued.ChallengeId, "967700889601", message.Code, "signIn:driver") is null,
            "OTP remains purpose bound");
        for (var attempt = 0; attempt < 5; attempt++)
            Check(store.Verify(issued.ChallengeId, "967700889601", "000000", "signIn:customer") is null, "incorrect OTP is rejected");
        Check(store.Verify(issued.ChallengeId, "967700889601", message.Code, "signIn:customer") is null,
            "five failures invalidate even the correct OTP");
        var valid = await delivery.CreateAsync("967700889602", "signIn:driver", pushToken, deviceId, "driver", default);
        var code = sender.Messages.Last().Code;
        var grant = store.Verify(valid.ChallengeId, "967700889602", code, "signIn:driver");
        Check(grant is not null && store.Verify(valid.ChallengeId, "967700889602", code, "signIn:driver") is null,
            "successful OTP is consumed once");
        Check(store.ConsumeVerificationToken(grant!, "967700889602", "signIn:driver") &&
            !store.ConsumeVerificationToken(grant!, "967700889602", "signIn:driver"), "verification grant is single use");
        var expired = await delivery.CreateAsync("967700889603", "signUp", pushToken, deviceId, "customer", default);
        var expiredCode = sender.Messages.Last().Code;
        clock.Advance(TimeSpan.FromMinutes(5));
        Check(store.Verify(expired.ChallengeId, "967700889603", expiredCode, "signUp") is null,
            "OTP expires at its five-minute deadline");
        sender.Failure = true;
        var failed = await delivery.CreateAsync("967700889604", "passwordReset", pushToken, deviceId, "customer", default);
        var failedMessage = sender.Messages.Last();
        Check(!failed.Success && failed.ErrorCode == "otp_push_delivery_failed" && failed.ChallengeId is null &&
            store.Verify(failedMessage.ChallengeId, "967700889604", failedMessage.Code, "passwordReset") is null,
            "push failure is explicit and cancels the generated challenge");
        sender.Failure = false;
        sender.Block = true;
        using var cancellation = new CancellationTokenSource();
        var pending = delivery.CreateAsync("967700889605", "signUp", pushToken, deviceId, "customer", cancellation.Token);
        await sender.Started.Task;
        cancellation.Cancel();
        try { await pending; throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        var cancelledMessage = sender.Messages.Last();
        Check(store.Verify(cancelledMessage.ChallengeId, "967700889605", cancelledMessage.Code, "signUp") is null,
            "cancelled delivery invalidates its generated OTP");
        sender.IgnoreCancellation = true;
        var timedOut = await delivery.CreateAsync("967700889609", "signUp", pushToken, deviceId, "customer", default);
        sender.Release.TrySetResult();
        sender.IgnoreCancellation = false;
        var timedOutMessage = sender.Messages.Last();
        Check(!timedOut.Success && timedOut.ErrorCode == "otp_push_delivery_failed" &&
            store.Verify(timedOutMessage.ChallengeId, "967700889609", timedOutMessage.Code, "signUp") is null,
            "the fifteen-second deadline bounds even an uncooperative sender and invalidates its OTP");
        Check(captured.ToString().Length == 0, "push delivery never prints raw OTP to the console");
        sender.Block = false;
        configuration["Otp:EnableTestPushDelivery"] = "false";
        var before = sender.Messages.Count;
        var off = await delivery.CreateAsync("967700889606", "signUp", pushToken, deviceId, "customer", default);
        Check(!off.Success && sender.Messages.Count == before, "runtime disabling stops push without recreating the service");
        var consoleStore = new OtpChallengeStore(consoleOtpEnabled: true);
        var consoleDelivery = new OtpDeliveryService(consoleStore, sender, configuration);
        var local = await consoleDelivery.CreateAsync("967700889607", "signUp", null, null, null, default);
        var localCode = Regex.Match(captured.ToString(), @": (\d{6})").Groups[1].Value;
        Check(local.Success && localCode.Length == 6 && sender.Messages.Count == before,
            "disabled push preserves existing development console OTP");
        configuration["Otp:EnableTestPushDelivery"] = "true";
        captured.GetStringBuilder().Clear();
        await consoleDelivery.CreateAsync("967700889608", "signUp", pushToken, deviceId, "customer", default);
        Check(captured.ToString().Length == 0, "push mode remains silent even when development console delivery is enabled");
    }
    finally { Console.SetOut(originalOutput); }
}

async Task FirebaseDryRunAsync()
{
    // Explicit preflight only: one read of a registered FCM target and an SDK
    // validation request. dryRun=true never sends an alert to that device.
    var runtimeIndex = Array.IndexOf(args, "--runtime-config");
    var runtimePath = runtimeIndex >= 0 && runtimeIndex + 1 < args.Length ? args[runtimeIndex + 1]
        : "E:/Driver/output/api-recovery-release-20261104/api/security/settings.json";
    try
    {
        var database = new DatabaseConfigurationStore(runtimePath);
        if (database.Current is null) { Console.WriteLine("SKIP: Firebase dry-run; runtime database configuration unavailable."); return; }
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddJsonFile(runtimePath, optional: false).Build();
        var options = new DbContextOptionsBuilder<YemenDriveDbContext>();
        new SqlServerDatabaseProvider().Configure(options, database.Current);
        await using var db = new YemenDriveDbContext(options.Options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registeredToken = await db.DevicePushInstallations.AsNoTracking()
            .Where(x => x.FcmToken != "").OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => x.FcmToken).FirstOrDefaultAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(registeredToken)) { Console.WriteLine("SKIP: Firebase dry-run; no registered device target."); return; }
        var runtimeRoot = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(runtimePath))!)!.FullName;
        foreach (var key in new[] { "GOOGLE_APPLICATION_CREDENTIALS", "Firebase:CredentialsPath" })
            if (configuration[key] is { Length: > 0 } credential && !Path.IsPathRooted(credential))
                configuration[key] = Path.Combine(runtimeRoot, credential);
        var sender = new FirebaseAdminPushSender(configuration);
        var getMessaging = typeof(FirebaseAdminPushSender).GetMethod("GetMessaging", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var messaging = (FirebaseMessaging)getMessaging.Invoke(sender, null)!;
        var synthetic = new OtpTestPushMessage(registeredToken, "synthetic-dry-run-challenge", "signIn:customer", "customer", DateTime.UtcNow.AddMinutes(5), "123456");
        var result = await messaging.SendAsync(FirebaseAdminPushSender.CreateTestOtpMessage(synthetic), dryRun: true, timeout.Token);
        Check(!string.IsNullOrWhiteSpace(result), "Firebase dry-run accepts temporary OTP payload");
        Console.WriteLine("PASS: Firebase dry-run validates credentials, network and OTP payload; no device notification sent.");
    }
    catch (Exception error)
    {
        // Do not print exception messages or transport objects: either may
        // include a token or credential path in provider diagnostics.
        var kind = error is FirebaseMessagingException firebase ? firebase.MessagingErrorCode.ToString() : error.GetType().Name;
        Console.WriteLine("FAIL: Firebase dry-run validation (" + kind + "); no device notification sent.");
        Environment.ExitCode = 1;
    }
}

async Task HttpAuthenticationContractsAsync()
{
    var databaseName = "YemenDrive_OtpPushTests_" + Guid.NewGuid().ToString("N");
    var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
    var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
    var settingsPath = Path.Combine(Path.GetTempPath(), databaseName + ".json");
    var databaseConfig = new DatabaseConfigurationStore(settingsPath);
    await databaseConfig.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB",
        Database = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }, default);
    WebApplication? app = null;
    try
    {
        await using var seed = new YemenDriveDbContext(options);
        await seed.Database.MigrateAsync();
        foreach (var (phone, role) in new[] { ("967700889611", UserRole.Customer), ("967700889612", UserRole.Driver), ("967700889613", UserRole.Admin) })
            seed.Users.Add(new() { PhoneNumber = phone, DisplayName = "Synthetic OTP test", Role = role,
                PasswordHash = PasswordHash.Create("synthetic-password-01"), IsActive = true });
        await seed.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Otp:EnableTestPushDelivery"] = "true";
        var sender = new MockPush();
        builder.Services.AddSingleton(databaseConfig);
        builder.Services.AddDbContext<YemenDriveDbContext>(db => db.UseSqlServer(connection));
        builder.Services.AddSingleton(new OtpChallengeStore(consoleOtpEnabled: false));
        builder.Services.AddSingleton<IOtpTestPushSender>(sender);
        builder.Services.AddSingleton<OtpDeliveryService>();
        builder.Services.AddScoped<TrustedDeviceCredentialService>();
        builder.Services.AddScoped<AuthSessionService>();
        builder.Services.AddScoped<FinancialAccountProvisioningService>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
        app = builder.Build();
        app.UseMiddleware<AuthSessionAuthenticationMiddleware>();
        app.MapAuthenticationEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-expired-auth-token");
        using (var capabilityResponse = await http.GetAsync("/api/auth/otp-delivery"))
        {
            var capability = await capabilityResponse.Content.ReadFromJsonAsync<JsonElement>();
            Check(capabilityResponse.StatusCode == HttpStatusCode.OK && capability.GetProperty("data").GetProperty("testPushEnabled").GetBoolean() &&
                capabilityResponse.Headers.CacheControl?.NoStore == true, "public capability bypasses stale auth and is not cached");
        }
        async Task<JsonElement> Post(string route, object data)
        {
            using var response = await http.PostAsJsonAsync(route, data);
            Check(response.IsSuccessStatusCode, "authentication test endpoint executes normally");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        object Login(string phone, string password, string client) => new { phoneNumber = phone, password,
            clientType = client, deviceId, otpPushToken = pushToken, notificationRenderer = "app" };
        var badPassword = await Post("/api/auth/login", Login("967700889611", "incorrect", "customer"));
        var badRole = await Post("/api/auth/login", Login("967700889611", "synthetic-password-01", "driver"));
        Check(badPassword.GetProperty("code").GetString() == "invalid_credentials" && badRole.GetProperty("code").GetString() == "account_type_mismatch" &&
            sender.Messages.Count == 0, "login validates password and account role before generating any OTP");
        foreach (var (phone, client) in new[] { ("967700889611", "customer"), ("967700889612", "driver") })
        {
            var login = await Post("/api/auth/login", Login(phone, "synthetic-password-01", client));
            var sent = sender.Messages.Last();
            Check(login.GetProperty("success").GetBoolean() && login.GetProperty("data").GetProperty("requiresOtp").GetBoolean() &&
                !login.ToString().Contains(sent.Code) && sent.Purpose == "signIn:" + client && sent.ClientType == client,
                "both client roles receive only their own pre-session sign-in challenge");
            Check(sent.NotificationRenderer == "app", "sign-in forwards the app notification renderer to OTP delivery");
        }
        Check(!await seed.AuthSessions.AnyAsync() && !await seed.TrustedDevices.AnyAsync(), "sending OTP does not create an authenticated session or trusted device");
        foreach (var client in new[] { "customer", "driver" })
        {
            var signup = await Post("/api/auth/register/request-otp", new { phoneNumber = "967700889620", clientType = client, deviceId, otpPushToken = pushToken, notificationRenderer = "app" });
            Check(signup.GetProperty("success").GetBoolean() && sender.Messages.Last().Purpose == "signUp" && sender.Messages.Last().ClientType == client,
                "signup OTP uses explicit requesting app scope without changing registration account contract");
            Check(sender.Messages.Last().NotificationRenderer == "app", "signup forwards the app notification renderer to OTP delivery");
        }
        foreach (var (phone, client) in new[] { ("967700889611", "customer"), ("967700889612", "driver") })
        {
            var reset = await Post("/api/auth/password/request-reset", new { identity = phone, channel = "sms", clientType = client, deviceId, otpPushToken = pushToken, notificationRenderer = "app" });
            Check(reset.GetProperty("success").GetBoolean() && sender.Messages.Last().Purpose == "passwordReset" && sender.Messages.Last().ClientType == client,
                "password reset pushes only to the matching rider or driver app scope");
            Check(sender.Messages.Last().NotificationRenderer == "app", "password reset forwards the app notification renderer to OTP delivery");
        }
        var beforeInvalid = sender.Messages.Count;
        var admin = await Post("/api/auth/password/request-reset", new { identity = "967700889613", channel = "sms", clientType = "customer", deviceId, otpPushToken = pushToken });
        var wrongReset = await Post("/api/auth/password/request-reset", new { identity = "967700889611", channel = "sms", clientType = "driver", deviceId, otpPushToken = pushToken });
        var noScope = await Post("/api/auth/register/request-otp", new { phoneNumber = "967700889620", deviceId, otpPushToken = pushToken });
        var noTarget = await Post("/api/auth/login", new { phoneNumber = "967700889611", password = "synthetic-password-01", clientType = "customer", deviceId });
        Check(admin.GetProperty("code").GetString() == "account_type_mismatch" && wrongReset.GetProperty("code").GetString() == "account_type_mismatch" &&
            noScope.GetProperty("code").GetString() == "invalid_client_type" && noTarget.GetProperty("code").GetString() == "otp_push_token_required" &&
            sender.Messages.Count == beforeInvalid, "admin, wrong account scope and absent push targets cannot receive test OTP");
        sender.Failure = true;
        var failed = await Post("/api/auth/login", Login("967700889611", "synthetic-password-01", "customer"));
        var failedMessage = sender.Messages.Last();
        Check(!failed.GetProperty("success").GetBoolean() && failed.GetProperty("code").GetString() == "otp_push_delivery_failed" &&
            failed.GetProperty("data").ValueKind == JsonValueKind.Null, "HTTP push failure does not claim an OTP delivery or expose challenge data");
        var verifyFailed = await Post("/api/auth/sign-in/verify-device", new { phoneNumber = "967700889611", code = failedMessage.Code,
            challengeId = failedMessage.ChallengeId, clientType = "customer", deviceId });
        Check(verifyFailed.GetProperty("code").GetString() == "invalid_otp", "a failed push's code cannot subsequently authorize a session");
        Check(!await seed.Notifications.AnyAsync() && !await seed.NotificationPushOutbox.AnyAsync() &&
            !await seed.NotificationPushDeliveries.AnyAsync() && !await seed.DevicePushInstallations.AnyAsync(),
            "OTP push never persists notifications, outbox deliveries or pre-session device installations");
        sender.Failure = false;
        await Post("/api/auth/login", Login("967700889611", "synthetic-password-01", "customer"));
        var validMessage = sender.Messages.Last();
        var verified = await Post("/api/auth/sign-in/verify-device", new { phoneNumber = "967700889611", code = validMessage.Code,
            challengeId = validMessage.ChallengeId, clientType = "customer", deviceId });
        Check(verified.GetProperty("success").GetBoolean() && verified.GetProperty("data").GetProperty("accessToken").GetString()!.Length > 0 &&
            await seed.AuthSessions.CountAsync() == 1, "the delivered OTP completes the existing device verification and session workflow");
        var replay = await Post("/api/auth/sign-in/verify-device", new { phoneNumber = "967700889611", code = validMessage.Code,
            challengeId = validMessage.ChallengeId, clientType = "customer", deviceId });
        Check(replay.GetProperty("code").GetString() == "invalid_otp" && await seed.AuthSessions.CountAsync() == 1,
            "replaying the delivered OTP cannot create another session");
        await Post("/api/auth/password/request-reset", new { identity = "967700889611", channel = "sms", clientType = "customer", deviceId, otpPushToken = pushToken });
        var resetMessage = sender.Messages.Last();
        var resetVerification = await Post("/api/auth/verify-otp", new { destination = "967700889611", code = resetMessage.Code,
            challengeId = resetMessage.ChallengeId, purpose = "passwordReset" });
        var resetComplete = await Post("/api/auth/password/reset", new { identity = "967700889611", newPassword = "synthetic-new-password-02",
            verificationToken = resetVerification.GetProperty("data").GetProperty("verificationToken").GetString() });
        Check(resetComplete.GetProperty("success").GetBoolean() && !await seed.AuthSessions.AnyAsync(x => x.RevokedAtUtc == null),
            "pushed reset OTP completes the existing password reset and session revocation workflow");
        builder.Configuration["Otp:EnableTestPushDelivery"] = "false";
        using var disabledResponse = await http.GetAsync("/api/auth/otp-delivery");
        var disabled = await disabledResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(!disabled.GetProperty("data").GetProperty("testPushEnabled").GetBoolean(), "capability immediately reflects server-side disabling");
        var count = sender.Messages.Count;
        var off = await Post("/api/auth/register/request-otp", new { phoneNumber = "967700889620" });
        Check(off.GetProperty("code").GetString() == "otp_delivery_unavailable" && sender.Messages.Count == count,
            "disabled production mode neither requires push target fields nor sends a test message");
    }
    finally
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        if (!databaseName.StartsWith("YemenDrive_OtpPushTests_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup target.");
        await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
        File.Delete(settingsPath);
    }
}

void Check(bool value, string label) { if (!value) throw new InvalidOperationException("FAIL: " + label); checks++; }
public sealed class MockPush : IOtpTestPushSender
{
    public List<OtpTestPushMessage> Messages { get; } = [];
    public bool Failure { get; set; }
    public bool Block { get; set; }
    public bool IgnoreCancellation { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<string> SendTestOtpAsync(OtpTestPushMessage message, CancellationToken token)
    {
        Messages.Add(message);
        if (Failure) throw new InvalidOperationException("Synthetic transport failure.");
        if (Block)
        {
            Started.TrySetResult();
            if (IgnoreCancellation) await Release.Task;
            else await Release.Task.WaitAsync(token);
        }
        return "synthetic-fcm-message-accepted";
    }
}
public sealed class ManualClock(DateTimeOffset instant) : TimeProvider
{
    private DateTimeOffset _now = instant;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
