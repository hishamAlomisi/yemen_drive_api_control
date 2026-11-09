using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using YemenDrive.Database.Configuration;
using YemenDrive.Api;
using YemenDrive.Api.Sms;
using YemenDrive.Application.Communication;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Security;

if (args.Contains("--runtime-preflight") || args.Contains("--apply-runtime-migration"))
{
    await RuntimeMigration.RunAsync(args.Contains("--apply-runtime-migration"));
    return;
}
if (args.Contains("--probe-runtime-sms-login"))
{
    // Read existing encrypted settings; print only connection state, never credentials or token.
    var runtimeRoot = @"E:\Driver\output\api-recovery-release-20261104\api";
    var settings = new ConfigurationBuilder().AddJsonFile(Path.Combine(runtimeRoot, "security", "settings.json"), optional: false).Build();
    var configuredKeys = settings["WalletLogin:KeyRingPath"];
    var keyPath = string.IsNullOrWhiteSpace(configuredKeys) ? Path.Combine(runtimeRoot, "security", "wallet-keys") : Path.GetFullPath(configuredKeys, runtimeRoot);
    var protectedServices = new ServiceCollection();
    var dataProtectionBuilder = protectedServices.AddDataProtection().SetApplicationName("YemenDrive.WalletLogin").PersistKeysToFileSystem(new DirectoryInfo(keyPath));
    if (OperatingSystem.IsWindows()) dataProtectionBuilder.ProtectKeysWithDpapi();
    await using var protectedProvider = protectedServices.BuildServiceProvider();
    var saved = new SmsProviderConfigurationStore(protectedProvider.GetRequiredService<IDataProtectionProvider>(), Path.Combine(runtimeRoot, "security", "sms-provider.dat"));
    using var providerHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    try
    {
        var result = await new SmsProviderRuntime(saved, providerHttp).ConnectAsync(default);
        Console.WriteLine(JsonSerializer.Serialize(result));
        Environment.ExitCode = result.Connected || result.RequiresActivation ? 0 : 1;
    }
    catch (Exception exception)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { state = "probe_failed", errorType = exception.GetType().Name }));
        Environment.ExitCode = 1;
    }
    return;
}

var checks = 0;
const string smsAppHash = "AbC12+/xYz9";
var retrieverText = OtpSmsFormatter.Format("تسجيل الدخول", "012345", smsAppHash);
Check(retrieverText.StartsWith("<#> ") && retrieverText.EndsWith("\n" + smsAppHash) && System.Text.Encoding.UTF8.GetByteCount(retrieverText) <= 140, "Retriever format fits 140 bytes and ends with the app hash");
foreach (var badHash in new[] { "", "short", "AbC12+/xYz9\n", "AbC12+/xYz9\r\nInjected" })
    Check(OtpSmsFormatter.Format("تسجيل الدخول", "012345", badHash) == OtpSmsFormatter.Format("تسجيل الدخول", "012345", null), "invalid hashes cannot add arbitrary SMS content");
var name = "YemenDrive_SmsDeliveryTests_" + Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), name);
Directory.CreateDirectory(root);
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={name};Integrated Security=True;TrustServerCertificate=True";
var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
var configuration = new SmsProviderConfigurationStore(dataProtection, Path.Combine(root, "sms.dat"));
var protector = new SmsPayloadProtector(dataProtection);
var provider = new FakeProvider();
using var http = new HttpClient(provider) { Timeout = TimeSpan.FromSeconds(2) };
var runtime = new SmsProviderRuntime(configuration, http);
var services = new ServiceCollection();
services.AddSingleton(configuration); services.AddSingleton(protector); services.AddSingleton(runtime);
services.AddScoped<WalletSmsInterceptor>();
services.AddDbContext<YemenDriveDbContext>((sp, options) => options.UseSqlServer(connection).AddInterceptors(sp.GetRequiredService<WalletSmsInterceptor>()));
services.AddSingleton<SmsDeliveryService>();
await using var serviceProvider = services.BuildServiceProvider();
var delivery = serviceProvider.GetRequiredService<SmsDeliveryService>();
var worker = new SmsDeliveryWorker(serviceProvider.GetRequiredService<IServiceScopeFactory>(), runtime, delivery, NullLogger<SmsDeliveryWorker>.Instance);
try
{
    Check((await runtime.ConnectAsync(default)).State == "unconfigured" && provider.Requests.Count == 0, "unconfigured runtime does not contact provider");
    await configuration.SaveAsync(new("https://example.test/api/login", "synthetic-user", "synthetic-password", "0018"), default);
    provider.ActivationRequired = true;
    var pending = await runtime.ConnectAsync(default);
    Check(pending.RequiresActivation && !pending.Connected, "Type 4 requires activation even with IsValid true and token");
    Check(!JsonSerializer.Serialize(pending).Contains("synthetic-token"), "status hides provider token");
    await runtime.ConnectAsync(default);
    Check(provider.LoginCount == 1, "activation polling does not issue another login or OTP");
    Check(!(await runtime.SendAsync(1, "775288883", "test", default)).Accepted && provider.SendCount == 0, "no messages before account activation");
    Check((await runtime.ActivateAsync("abc", default)).RequiresActivation && provider.ActivationCount == 0, "invalid activation format is rejected locally");
    provider.InvalidActivation = true;
    Check((await runtime.ActivateAsync("111111", default)).RequiresActivation, "incorrect activation preserves pending state");
    provider.InvalidActivation = false;
    Check((await runtime.ActivateAsync("724245", default)).Connected && provider.LoginCount == 2, "successful activation immediately obtains a fresh login token");
    var activation = provider.Requests.Last(x => x.Path.EndsWith("authenticator"));
    Check(activation.Token == "synthetic-token-1" && activation.Body.GetProperty("ActivationCode").ValueKind == JsonValueKind.Number, "authenticator uses numeric code and token header");
    var login = provider.Requests.First();
    Check(login.Path == "/api/login" && login.Token is null && login.Body.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "DeviceID", "Password", "UserName" }), "login URL and exact provider fields include DeviceID");
    Check(login.Body.GetProperty("DeviceID").GetString() == "0018" && login.Body.GetProperty("Password").GetString() == "synthetic-password", "saved device text and password are sent unchanged");
    provider.InvalidTokenOnce = true;
    Check((await runtime.SendAsync(123, "775288883", "مرحبا", default)).Accepted && provider.LoginCount == 3, "401 renews token and retries once");
    var sends = provider.Requests.Where(x => x.Path.EndsWith("sendSMSMsg")).TakeLast(2).ToArray();
    Check(sends[0].Body.GetProperty("id").GetInt64() == sends[1].Body.GetProperty("id").GetInt64() && sends[1].Token == "synthetic-token-3", "authenticated retry keeps message identity with refreshed token");
    await configuration.SaveAsync(new("https://example.test/api", "updated-user", "", "0019"), default);
    Check((await runtime.ConnectAsync(default)).Connected && provider.LoginCount == 4, "changed configuration invalidates cached session");
    provider.FalseValid = true;
    Check(!(await runtime.ConnectAsync(default, force: true)).Connected, "IsValid false cannot establish login despite Type 2 and token");
    provider.FalseValid = false;
    provider.EmptyToken = true;
    Check(!(await runtime.ConnectAsync(default, force: true)).Connected, "empty token cannot establish login");
    provider.EmptyToken = false;
    provider.MalformedLogin = true;
    Check(!(await runtime.ConnectAsync(default, force: true)).Connected, "malformed provider response fails safely");
    provider.MalformedLogin = false;
    Check((await runtime.ConnectAsync(default, force: true)).Connected, "connection recovers after invalid login");

    foreach (var phone in new[] { "775288883", "+967 775288883", "00967775288883", "967775288883", "٧٧٥٢٨٨٨٨٣" })
        Check(SmsPayloadProtector.NormalizeNumber(phone) == "775288883", "Yemeni number normalized without country code");
    Check(SmsPayloadProtector.NormalizeNumber("user@example.test") is null && SmsPayloadProtector.NormalizeNumber("+966775288883") is null, "invalid destination and another country rejected");
    await using (var setup = serviceProvider.CreateAsyncScope())
    {
        var db = setup.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        await db.Database.MigrateAsync();
        Check(!db.Database.HasPendingModelChanges(), "generated migration matches model");
        db.Users.Add(new User { PhoneNumber = "+967775288883", Email = "synthetic@example.test", Role = UserRole.Customer, IsActive = true, PasswordHash = PasswordHash.Create("synthetic-password"), Wallet = new Wallet() });
        await db.SaveChangesAsync();
    }
    var challengeStore = new OtpChallengeStore(false);
    var push = new ForbiddenPush();
    var otp = new OtpDeliveryService(challengeStore, push, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Otp:EnableTestPushDelivery"] = "true" }).Build(), delivery);
    var issued = await otp.CreateAsync("+967775288883", "signIn:customer", null, null, null, default);
    Check(issued.Success && push.Calls == 0, "configured SMS bypasses test push and its FCM requirements");
    var sentOtp = provider.Requests.Last(x => x.Path.EndsWith("sendSMSMsg"));
    var otpCode = System.Text.RegularExpressions.Regex.Match(sentOtp.Body.GetProperty("Text").GetString()!, @"\b\d{6}\b").Value;
    Check(otpCode.Length == 6 && challengeStore.Verify(issued.ChallengeId, "+967775288883", otpCode, "signIn:customer") is not null, "provider receives a usable OTP without exposing it in auth result");
    Check(sentOtp.Body.GetProperty("Numbers")[0].ValueKind == JsonValueKind.String && sentOtp.Body.GetProperty("Numbers")[0].GetString() == "775288883" && sentOtp.Body.GetProperty("id").GetInt64() >= 1000000000000, "send payload has normalized string array and durable numeric identity");
    Check(!JsonSerializer.Serialize(issued).Contains(otpCode), "OTP response excludes code");
    var reset = await otp.CreateAsync("synthetic@example.test", "passwordReset", null, null, null, default);
    Check(reset.Success && provider.Requests.Last().Body.GetProperty("Numbers")[0].GetString() == "775288883", "email recovery resolves registered phone while challenge retains identity");
    Check((await otp.CreateAsync("775288883", "signUp", null, null, null, default)).Success, "registration OTP uses SMS");
    provider.RejectSend = true;
    var failed = await otp.CreateAsync("775288883", "signIn:driver", null, null, null, default);
    Check(!failed.Success && failed.ChallengeId is null && push.Calls == 0, "rejected SMS cancels challenge without test push fallback");
    provider.RejectSend = false;
    provider.TimeoutSend = true;
    var beforeTimeout = provider.SendCount;
    Check(!(await otp.CreateAsync("775288883", "signUp", null, null, null, default)).Success && provider.SendCount == beforeTimeout + 1, "uncertain delivery is not resent");
    provider.TimeoutSend = false;
    await using (var read = serviceProvider.CreateAsyncScope())
    {
        var db = read.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var report = await new SmsStatisticsService(db).GetAsync(default);
        Check(report.TotalCount == 3 && report.SignInCount == 1 && report.PasswordResetCount == 1 && report.RegistrationCount == 1, "report counts accepted OTPs only");
        Check(await db.SmsDispatches.AllAsync(x => x.ProtectedPayload == null), "completed payloads and OTPs are erased");
        Check(await db.SmsDispatches.AnyAsync(x => x.State == SmsDispatchState.Unconfirmed), "uncertain outcome retained separately");
    }
    var beforeWallet = provider.SendCount;
    await using (var rolledBack = serviceProvider.CreateAsyncScope())
    {
        var db = rolledBack.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var wallet = await db.Wallets.SingleAsync();
        db.WalletTransactions.Add(new() { WalletId = wallet.Id, Type = WalletTransactionType.Credit, Amount = 10, BalanceAfter = 10 });
        await db.SaveChangesAsync();
        Check(provider.SendCount == beforeWallet, "wallet save performs no provider I/O");
        await tx.RollbackAsync();
    }
    await using (var verifyRollback = serviceProvider.CreateAsyncScope())
    {
        var db = verifyRollback.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        Check(!await db.SmsDispatches.AnyAsync(x => x.Purpose == SmsMessagePurpose.WalletMovement), "rolled back wallet has no SMS outbox row");
    }
    await using (var committed = serviceProvider.CreateAsyncScope())
    {
        var db = committed.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var wallet = await db.Wallets.SingleAsync();
        foreach (var type in Enum.GetValues<WalletTransactionType>()) db.WalletTransactions.Add(new() { WalletId = wallet.Id, Type = type, Amount = 10, BalanceAfter = 100, Description = "synthetic" });
        await db.SaveChangesAsync();
        await db.SaveChangesAsync();
        var rows = await db.SmsDispatches.Where(x => x.Purpose == SmsMessagePurpose.WalletMovement).ToArrayAsync();
        Check(rows.Length == 6 && rows.All(x => x.WalletTransactionId is > 0), "all six wallet movement kinds queued once with committed foreign key");
        Check(rows.All(x => !x.ProtectedPayload!.Contains("775288883") && !x.ProtectedPayload.Contains("يمن")), "wallet SMS payload encrypted at rest");
    }
    await worker.TickAsync(default);
    await worker.TickAsync(default);
    Check(provider.SendCount == beforeWallet + 6, "worker delivers all wallet movements once after commit");
    var walletSends = provider.Requests.Where(x => x.Path.EndsWith("sendSMSMsg")).TakeLast(6).ToArray();
    Check(walletSends.Any(x => x.Body.GetProperty("Text").GetString()!.Contains("تم حجز")) && walletSends.Any(x => x.Body.GetProperty("Text").GetString()!.Contains("تم تحرير حجز")), "hold and release have correct wording");
    var ids = provider.Requests.Where(x => x.Path.EndsWith("sendSMSMsg")).Skip(2).Select(x => x.Body.GetProperty("id").GetInt64()).ToArray();
    Check(ids.Distinct().Count() == ids.Length, "every new logical message gets a different persisted numeric ID");
    await using (var final = serviceProvider.CreateAsyncScope())
    {
        var db = final.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        Check((await new SmsStatisticsService(db).GetAsync(default)).WalletMovementCount == 6 && await db.SmsMessageLogs.CountAsync() == 9, "accepted wallet messages integrated with statistics");
        var priorId = await db.SmsDispatches.Where(x => x.State == SmsDispatchState.Sent).Select(x => x.Id).FirstAsync();
        var count = provider.SendCount;
        await delivery.DeliverAsync(db, priorId, default);
        Check(provider.SendCount == count, "processed dispatch cannot be sent again");
        db.SmsDispatches.Add(new() { Purpose = SmsMessagePurpose.WalletMovement, CreatedAtUtc = DateTime.UtcNow.AddDays(-2), ExpiresAtUtc = DateTime.UtcNow.AddDays(-1), ProtectedPayload = protector.Protect("775288883", "expired") });
        db.SmsDispatches.Add(new() { Purpose = SmsMessagePurpose.WalletMovement, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5), ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-5), State = SmsDispatchState.Sending, ProtectedPayload = protector.Protect("775288883", "uncertain") });
        await db.SaveChangesAsync();
        await worker.TickAsync(default);
        Check(provider.SendCount == count && await db.SmsDispatches.AnyAsync(x => x.State == SmsDispatchState.Expired) && !await db.SmsDispatches.AnyAsync(x => x.ProtectedPayload != null), "expired and interrupted messages cleared without duplicate delivery");
    }
    await HttpContractsAsync();
    Console.WriteLine($"PASS: {checks} SMS delivery checks; mocked provider and migrated disposable LocalDB, no real SMS sent.");
}
finally
{
    await using var cleanup = serviceProvider.CreateAsyncScope();
    await cleanup.ServiceProvider.GetRequiredService<YemenDriveDbContext>().Database.EnsureDeletedAsync();
    if (Path.GetFileName(Path.GetFullPath(root)) != name || !Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup target");
    Directory.Delete(root, recursive: true);
}
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }

async Task HttpContractsAsync()
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(configuration); builder.Services.AddSingleton(runtime); builder.Services.AddSingleton(delivery);
    builder.Services.AddSingleton(new OtpChallengeStore(false));
    builder.Services.AddSingleton<IOtpTestPushSender>(new ForbiddenPush());
    builder.Services.AddSingleton<OtpDeliveryService>();
    builder.Services.AddDbContext<YemenDriveDbContext>(options => options.UseSqlServer(connection));
    builder.Services.AddScoped<TrustedDeviceCredentialService>(); builder.Services.AddScoped<AuthSessionService>();
    builder.Services.AddScoped<FinancialAccountProvisioningService>(); builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<YemenDrive.Shared.Security.ICurrentUserContext, TestActor>();
    builder.Configuration["Otp:EnableTestPushDelivery"] = "true";
    await using var app = builder.Build(); app.MapAuthenticationEndpoints(); app.MapSmsProviderEndpoints(); app.MapSmsManualEndpoints();
    await app.StartAsync();
    try
    {
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var capability = await client.GetFromJsonAsync<JsonElement>("/api/auth/otp-delivery");
        Check(capability.GetProperty("data").GetProperty("smsEnabled").GetBoolean() && !capability.GetProperty("data").GetProperty("testPushEnabled").GetBoolean(), "HTTP capability disables FCM requirements when SMS is configured");
        var loginResponse = await Post("/api/auth/login", new { phoneNumber = "+967775288883", password = "synthetic-password", deviceId = "sms-test-device-001", clientType = "customer", smsAppHash });
        Check(loginResponse.GetProperty("success").GetBoolean() && loginResponse.GetProperty("data").GetProperty("requiresOtp").GetBoolean(), "HTTP login on new device requests real SMS OTP without FCM fields");
        Check(provider.Requests.Last().Body.GetProperty("Text").GetString()!.EndsWith("\n" + smsAppHash), "login forwards app hash to SMS transport");
        var code = System.Text.RegularExpressions.Regex.Match(provider.Requests.Last().Body.GetProperty("Text").GetString()!, @"\b\d{6}\b").Value;
        var verified = await Post("/api/auth/sign-in/verify-device", new { phoneNumber = "+967775288883", code, challengeId = loginResponse.GetProperty("data").GetProperty("challengeId").GetString(), deviceId = "sms-test-device-001", clientType = "customer" });
        Check(verified.GetProperty("success").GetBoolean() && verified.GetProperty("data").TryGetProperty("accessToken", out _), "HTTP OTP verification issues session on trusted device");
        var recovery = await Post("/api/auth/password/request-reset", new { identity = "synthetic@example.test", channel = "sms", smsAppHash });
        Check(recovery.GetProperty("success").GetBoolean(), "HTTP password recovery works with existing app fields and SMS despite legacy FCM setting");
        Check(provider.Requests.Last().Body.GetProperty("Text").GetString()!.EndsWith("\n" + smsAppHash), "password recovery forwards app hash to SMS transport");
        code = System.Text.RegularExpressions.Regex.Match(provider.Requests.Last().Body.GetProperty("Text").GetString()!, @"\b\d{6}\b").Value;
        var confirmation = await Post("/api/auth/verify-otp", new { destination = "synthetic@example.test", purpose = "passwordReset", code });
        var reset = await Post("/api/auth/password/reset", new { identity = "synthetic@example.test", verificationToken = confirmation.GetProperty("data").GetProperty("verificationToken").GetString(), newPassword = "synthetic-password-2" });
        Check(reset.GetProperty("success").GetBoolean(), "SMS email recovery completes OTP and password reset without client changes");
        var registration = await Post("/api/auth/register/request-otp", new { phoneNumber = "775288883", smsAppHash });
        Check(registration.GetProperty("success").GetBoolean() && provider.Requests.Last().Body.GetProperty("Text").GetString()!.EndsWith("\n" + smsAppHash), "registration forwards app hash to SMS transport");
        foreach (var path in new[] { "/api/admin/sms-provider/connection", "/api/admin/sms-provider/activate", "/api/admin/sms-provider/connect" })
        {
            using var response = path.EndsWith("connection") ? await client.GetAsync(path) : await client.PostAsJsonAsync(path, new { code = "724245" });
            Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true, "anonymous provider access rejected: " + path);
        }
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
        var admin = new User { PhoneNumber = "775288881", Role = UserRole.Admin, IsSystemAdministrator = true };
        var reader = new User { PhoneNumber = "775288882", Role = UserRole.Admin };
        db.Users.AddRange(admin, reader); await db.SaveChangesAsync();
        db.AdministratorPermissions.Add(new() { UserId = reader.Id, Permission = "policy.read" }); await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Add("X-Test-Actor", reader.Id.ToString());
        using (var response = await client.PostAsJsonAsync("/api/admin/sms-provider/activate", new { code = "724245" }))
            Check(response.StatusCode == HttpStatusCode.Forbidden, "read-only admin cannot activate provider");
        client.DefaultRequestHeaders.Remove("X-Test-Actor"); client.DefaultRequestHeaders.Add("X-Test-Actor", admin.Id.ToString());
        provider.ActivationRequired = true;
        var reconnect = await Post("/api/admin/sms-provider/connect", new { });
        Check(reconnect.GetProperty("data").GetProperty("requiresActivation").GetBoolean(), "admin reconnect exposes activation state");
        var active = await Post("/api/admin/sms-provider/activate", new { code = "724245" });
        Check(active.GetProperty("data").GetProperty("connected").GetBoolean() && !active.ToString().Contains("synthetic-token"), "authenticated activation completes without returning token");

        var beforeManual = provider.SendCount;
        client.DefaultRequestHeaders.Remove("X-Test-Actor");
        using (var response = await client.GetAsync("/api/admin/sms-send")) Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true, "anonymous cannot inspect manual SMS state");
        using (var response = await client.PostAsJsonAsync("/api/admin/sms-send", new ManualSmsInput(new[] { "775288883" }, "synthetic", Guid.NewGuid().ToString()))) Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous cannot send SMS");
        client.DefaultRequestHeaders.Add("X-Test-Actor", reader.Id.ToString());
        using (var response = await client.PostAsJsonAsync("/api/admin/sms-send", new ManualSmsInput(new[] { "775288883" }, "synthetic", Guid.NewGuid().ToString()))) Check(response.StatusCode == HttpStatusCode.Forbidden, "read-only administrator cannot send SMS");
        client.DefaultRequestHeaders.Remove("X-Test-Actor"); client.DefaultRequestHeaders.Add("X-Test-Actor", admin.Id.ToString());
        var loggedIn = provider.LoginCount;
        var single = new ManualSmsInput(new[] { "775288883" }, "رسالة يدوية تجريبية", Guid.NewGuid().ToString());
        var singleResult = await Post("/api/admin/sms-send", single);
        Check(singleResult.GetProperty("data").GetProperty("accepted").GetBoolean() && provider.SendCount == beforeManual + 1 && provider.LoginCount == loggedIn, "single manual SMS uses existing provider session");
        var bulk = new ManualSmsInput(new[] { "+967775288883", "775288883", "٧٧٥٢٨٨٨٨٤" }, "سطر أول\nسطر ثانٍ", Guid.NewGuid().ToString());
        var bulkResult = await Post("/api/admin/sms-send", bulk);
        var capture = provider.Requests.Last();
        Check(bulkResult.GetProperty("data").GetProperty("recipientCount").GetInt32() == 2 && capture.Body.GetProperty("Numbers").GetArrayLength() == 2 && capture.Body.GetProperty("Numbers")[0].GetString() == "775288883", "multiple numbers normalize and deduplicate in one provider request");
        Check(capture.Body.GetProperty("Text").GetString() == bulk.Text && capture.Body.GetProperty("id").GetInt64() != singleResult.GetProperty("data").GetProperty("messageId").GetInt64(), "manual text preserved and new message ID is numeric and unique");
        var countBeforeReplay = provider.SendCount;
        var replay = await Post("/api/admin/sms-send", bulk);
        Check(replay.GetProperty("data").GetProperty("messageId").GetInt64() == bulkResult.GetProperty("data").GetProperty("messageId").GetInt64() && provider.SendCount == countBeforeReplay, "replayed request does not send another SMS");
        using (var conflict = await client.PostAsJsonAsync("/api/admin/sms-send", bulk with { Text = "different message" })) Check(conflict.StatusCode == HttpStatusCode.BadRequest && provider.SendCount == countBeforeReplay, "request ID cannot be reused with different message");
        var concurrentInput = single with { RequestKey = Guid.NewGuid().ToString() };
        var concurrentCount = provider.SendCount;
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Post("/api/admin/sms-send", concurrentInput)));
        Check(provider.SendCount == concurrentCount + 1 && concurrent.All(x => x.GetProperty("success").GetBoolean()), "concurrent copies of same manual request send only once");
        var validationCount = provider.SendCount;
        foreach (var invalid in new[] { single with { Numbers = Array.Empty<string>() }, single with { Numbers = new[] { "775288883", "bad-number" } }, single with { Numbers = Enumerable.Repeat("775288883", 101).ToArray() }, single with { Text = " " }, single with { Text = new string('a', 1601) }, single with { RequestKey = "invalid" } })
        {
            using var response = await client.PostAsJsonAsync("/api/admin/sms-send", invalid);
            Check(response.StatusCode == HttpStatusCode.BadRequest && provider.SendCount == validationCount, "invalid manual input rejected without partial sending");
        }
        provider.RejectSend = true;
        var rejected = await Post("/api/admin/sms-send", single with { RequestKey = Guid.NewGuid().ToString() });
        Check(!rejected.GetProperty("data").GetProperty("accepted").GetBoolean() && rejected.GetProperty("data").GetProperty("state").GetString() == "rejected", "provider rejection shown as failed manual outcome");
        provider.RejectSend = false;
        provider.TimeoutSend = true;
        var uncertainInput = single with { RequestKey = Guid.NewGuid().ToString() };
        var uncertain = await Post("/api/admin/sms-send", uncertainInput);
        var uncertainCount = provider.SendCount;
        await Post("/api/admin/sms-send", uncertainInput);
        Check(uncertain.GetProperty("data").GetProperty("uncertain").GetBoolean() && provider.SendCount == uncertainCount, "uncertain manual delivery is never retried blindly");
        provider.TimeoutSend = false;
        provider.ActivationRequired = true;
        await Post("/api/admin/sms-provider/connect", new { });
        var activationCount = provider.SendCount;
        using (var response = await client.PostAsJsonAsync("/api/admin/sms-send", single with { RequestKey = Guid.NewGuid().ToString() })) Check(response.StatusCode == HttpStatusCode.BadRequest && provider.SendCount == activationCount, "manual sending requires provider activation");
        await Post("/api/admin/sms-provider/activate", new { code = "724245" });
        db.ChangeTracker.Clear();
        Check((await new SmsStatisticsService(db).GetAsync(default)).ManualCount == 4, "report counts accepted manual recipients only");
        Check(!await db.SmsDispatches.AnyAsync(x => x.Purpose == SmsMessagePurpose.Manual && x.ProtectedPayload != null), "completed manual message contents are erased");

        async Task<JsonElement> Post(string path, object body) { using var response = await client.PostAsJsonAsync(path, body); return await response.Content.ReadFromJsonAsync<JsonElement>(); }
    }
    finally { await app.StopAsync(); }
}

sealed class TestActor(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor) : YemenDrive.Shared.Security.ICurrentUserContext
{
    public int? UserId => int.TryParse(accessor.HttpContext?.Request.Headers["X-Test-Actor"].ToString(), out var id) ? id : null;
}

sealed class ForbiddenPush : IOtpTestPushSender
{
    public int Calls;
    public Task<string> SendTestOtpAsync(OtpTestPushMessage message, CancellationToken cancellationToken) { Calls++; throw new Exception("Unexpected test push"); }
}
sealed record Captured(string Path, string? Token, JsonElement Body);
sealed class FakeProvider : HttpMessageHandler
{
    public List<Captured> Requests = [];
    public int LoginCount, ActivationCount, SendCount;
    public bool ActivationRequired, InvalidActivation, InvalidTokenOnce, FalseValid, EmptyToken, RejectSend, TimeoutSend, MalformedLogin;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var token = request.Headers.TryGetValues("token", out var values) ? values.Single() : null;
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Requests.Add(new(path, token, body));
        if (path == "/api/login" && (!body.TryGetProperty("DeviceID", out var device) || string.IsNullOrWhiteSpace(device.GetString())))
            return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "Missing DeviceID" }) };
        if (path == "/api/login") { LoginCount++; return MalformedLogin ? Json(new[] { "bad" }) : Json(new { IsValid = !FalseValid, Type = ActivationRequired ? 4 : 2, Token = EmptyToken ? "" : "synthetic-token-" + LoginCount }); }
        if (path == "/api/authenticator") { ActivationCount++; if (!InvalidActivation) ActivationRequired = false; return Json(new { Status = !InvalidActivation }); }
        if (path == "/api/sendSMSMsg")
        {
            SendCount++;
            if (InvalidTokenOnce) { InvalidTokenOnce = false; return new(HttpStatusCode.Unauthorized); }
            if (TimeoutSend) throw new TaskCanceledException("synthetic timeout");
            return Json(new { Status = !RejectSend });
        }
        throw new Exception("Unexpected URL: " + path);
    }
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = null }) };
}
