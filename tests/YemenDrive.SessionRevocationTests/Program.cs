using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using YemenDrive.Api;
using YemenDrive.Api.Realtime;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Database.Realtime;

var database = "YemenDrive_SessionRevocationTests_" + Guid.NewGuid().ToString("N");
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={database};Integrated Security=True;TrustServerCertificate=True";
var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
var configPath = Path.Combine(Path.GetTempPath(), database + ".json");
var configuration = new DatabaseConfigurationStore(configPath);
await configuration.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB",
    Database = database, IntegratedSecurity = true, TrustServerCertificate = true }, default);
try
{
    await using var seed = new YemenDriveDbContext(options);
    await seed.Database.MigrateAsync();
    var first = new User { PhoneNumber = "revoke-first", Role = UserRole.Customer };
    var other = new User { PhoneNumber = "revoke-other", Role = UserRole.Customer };
    var admin = new User { PhoneNumber = "revoke-admin", Role = UserRole.Admin };
    var observer = new User { PhoneNumber = "revoke-observer", Role = UserRole.Admin };
    seed.Users.AddRange(first, other, admin, observer); await seed.SaveChangesAsync();
    var initial = new AuthSessionService();
    var firstOne = await initial.CreateAsync(seed, first, "first-1", default);
    var firstTwo = await initial.CreateAsync(seed, first, "first-2", default);
    var otherToken = await initial.CreateAsync(seed, other, "other", default);
    var adminOne = await initial.CreateAsync(seed, admin, "admin-1", default);
    var adminTwo = await initial.CreateAsync(seed, admin, "admin-2", default);
    var observerToken = await initial.CreateAsync(seed, observer, "observer", default);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.GetTempPath(), ApplicationName = typeof(TrackingHub).Assembly.FullName });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(configuration);
    builder.Services.AddSingleton<IApiReadinessProbe, DatabaseReadinessProbe>();
    builder.Services.AddSingleton<ApiReadinessService>();
    builder.Services.AddTrackingSessionRevocation();
    builder.Services.AddSingleton(new RealtimeJournalSettings { Enabled = true });
    builder.Services.AddSingleton<RealtimeCommitSignal>();
    builder.Services.AddSingleton<RealtimeCommitInterceptor>();
    builder.Services.AddScoped<RealtimeStateRecovery>();
    var refreshReadBarrier = new RefreshReadBarrier();
    builder.Services.AddDbContext<YemenDriveDbContext>((sp, db) => {
        db.UseSqlServer(connection).AddTrackingSessionRevocation(sp);
        db.AddInterceptors(refreshReadBarrier, sp.GetRequiredService<RealtimeCommitInterceptor>());
    });
    builder.Services.AddScoped<AuthSessionService>();
    builder.Services.AddScoped<TrackingHubAccessPolicy>();
    builder.Services.AddScoped<DriverLocationIngestionService>();
    builder.Services.AddSingleton<NearbyDriverSubscriptionStore>();
    builder.Services.AddSingleton<RideParticipantPresence>();
    builder.Services.AddScoped<IRideRealtimePublisher, SignalRRideRealtimePublisher>();
    await using var app = builder.Build();
    app.UseMiddleware<AuthSessionAuthenticationMiddleware>();
    app.MapApiReadiness();
    app.MapHub<TrackingHub>("/hubs/tracking", o => o.CloseOnAuthenticationExpiration = true);
    await app.StartAsync();
    try
    {
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + '/';
        using (var http = new HttpClient { BaseAddress = new Uri(url) })
        {
            using var ordinary = await http.GetAsync("hubs/tracking?transport=WebSockets&access_token=" + Uri.EscapeDataString(firstOne.AccessToken));
            Check(ordinary.StatusCode == HttpStatusCode.Unauthorized, "a transport query hint cannot authenticate an ordinary HTTP request");
            using var anonymousNegotiate = await http.PostAsync("hubs/tracking/negotiate?negotiateVersion=1&access_token=" + Uri.EscapeDataString(firstOne.AccessToken), null);
            Check(anonymousNegotiate.StatusCode == HttpStatusCode.Unauthorized, "query tokens cannot authenticate negotiation");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "expired-test-session");
            using var health = await http.GetAsync("api/health/ready");
            var ready = await health.Content.ReadFromJsonAsync<JsonElement>();
            Check(health.IsSuccessStatusCode && ready.GetProperty("success").GetBoolean() && ready.GetProperty("data").GetProperty("ready").GetBoolean() &&
                health.Headers.CacheControl?.NoStore == true, "anonymous readiness tolerates a stale session and checks the configured SQL database");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstOne.AccessToken);
            using var negotiated = await http.PostAsync("hubs/tracking/negotiate?negotiateVersion=1", null);
            negotiated.EnsureSuccessStatusCode();
            var details = await negotiated.Content.ReadFromJsonAsync<JsonElement>();
            var streamUrl = "hubs/tracking?id=" + Uri.EscapeDataString(details.GetProperty("connectionToken").GetString()!) +
                "&access_token=" + Uri.EscapeDataString(firstOne.AccessToken);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-test-session");
            using var badBearer = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            badBearer.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var denied = await http.SendAsync(badBearer, HttpCompletionOption.ResponseHeadersRead);
            Check(denied.StatusCode == HttpStatusCode.Unauthorized, "a query token cannot replace a supplied invalid Authorization header");
            http.DefaultRequestHeaders.Authorization = null;
            using var streamRequest = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            streamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var stream = await http.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
            Check(stream.IsSuccessStatusCode && stream.Content.Headers.ContentType?.MediaType == "text/event-stream",
                "browser EventSource fallback authenticates without a transport query or Authorization header");
        }
        await using var socket1 = await IgnoringSignalRClient.ConnectAsync(url, firstOne.AccessToken);
        await using var socket2 = await IgnoringSignalRClient.ConnectAsync(url, firstTwo.AccessToken);
        await using var socketOther = await IgnoringSignalRClient.ConnectAsync(url, otherToken.AccessToken);
        await using var socketAdmin1 = await IgnoringSignalRClient.ConnectAsync(url, adminOne.AccessToken);
        await using var socketAdmin2 = await IgnoringSignalRClient.ConnectAsync(url, adminTwo.AccessToken);
        await using var socketObserver = await IgnoringSignalRClient.ConnectAsync(url, observerToken.AccessToken);
        await socketAdmin1.InvokeAsync("JoinOperations"); await socketAdmin2.InvokeAsync("JoinOperations");
        await socketObserver.InvokeAsync("JoinOperations");
        var hub = app.Services.GetRequiredService<IHubContext<TrackingHub>>();
        var registry = app.Services.GetRequiredService<TrackingConnectionRegistry>();
        Check(registry.Count == 6, "six browser-style WebSockets without transport query authenticate and register independently of client cooperation");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await scope.ServiceProvider.GetRequiredService<AuthSessionService>().RevokeCurrentAsync(db, firstOne.AccessToken, default);
            await hub.Clients.Group($"user:{first.Id}").SendAsync("beforeRollback", new { ok = true });
            await Until(() => socket1.Events.Any(x => x.Name == "beforeRollback"));
            Check(!socket1.Closed.IsCompleted, "a staged family logout does not abort or revoke passive delivery before commit");
            await tx.RollbackAsync();
        }
        await socket1.InvokeAsync("SetForeground", true);
        Check(await initial.AuthenticateAsync(seed, firstOne.AccessToken, default) is not null, "rolling back family logout preserves both SQL authentication and the live socket");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await scope.ServiceProvider.GetRequiredService<AuthSessionService>().RevokeCurrentAsync(db, firstOne.AccessToken, default);
            await tx.CommitAsync();
        }
        await socket1.Closed.WaitAsync(TimeSpan.FromSeconds(3));
        var closedCount = socket1.Events.Count;
        await hub.Clients.Group($"user:{first.Id}").SendAsync("afterFamilyLogout", new { ok = true });
        await Until(() => socket2.Events.Any(x => x.Name == "afterFamilyLogout"));
        await socket2.InvokeAsync("SetForeground", true); await socketOther.InvokeAsync("SetForeground", true);
        Check(socket1.Events.Count == closedCount && !socket2.Closed.IsCompleted && !socketOther.Closed.IsCompleted,
            "committed family logout closes an ignoring client and leaves its other family and unrelated user connected");
        Check(await initial.AuthenticateAsync(seed, firstOne.AccessToken, default) is null &&
            await initial.AuthenticateAsync(seed, firstTwo.AccessToken, default) is not null, "family logout invalidates only that family's credentials");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            (await db.Users.SingleAsync(x => x.Id == first.Id)).DisplayName = "renamed";
            await db.SaveChangesAsync();
        }
        await socket2.InvokeAsync("SetForeground", true);
        Check(!socket2.Closed.IsCompleted, "ordinary profile edits retain valid existing sessions");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            (await db.Users.SingleAsync(x => x.Id == admin.Id)).Role = UserRole.Customer;
            await db.SaveChangesAsync();
            Check(!await db.AuthSessions.AnyAsync(x => x.UserId == admin.Id && x.RevokedAtUtc == null),
                "demotion writes user access and all session revocations within the same SQL transaction");
            await hub.Clients.Group("admin:operations").SendAsync("demotionRollback", new { ok = true });
            await Until(() => socketAdmin1.Events.Any(x => x.Name == "demotionRollback"));
            await tx.RollbackAsync();
        }
        await socketAdmin1.InvokeAsync("JoinOperations"); await socketAdmin2.InvokeAsync("JoinOperations");
        Check(await initial.AuthenticateAsync(seed, adminOne.AccessToken, default) is not null &&
            !socketAdmin1.Closed.IsCompleted && !socketAdmin2.Closed.IsCompleted, "rolled-back demotion neither revokes SQL sessions nor aborts either administrator socket");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            (await db.Users.SingleAsync(x => x.Id == admin.Id)).Role = UserRole.Customer;
            await db.SaveChangesAsync(); await tx.CommitAsync();
        }
        await Task.WhenAll(socketAdmin1.Closed, socketAdmin2.Closed).WaitAsync(TimeSpan.FromSeconds(3));
        var deniedCount = socketAdmin1.Events.Count + socketAdmin2.Events.Count;
        var delivery = new RealtimeStateDeliveryWorker(app.Services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<RealtimeCommitSignal>(), app.Services.GetRequiredService<RealtimeJournalSettings>(),
            configuration, hub, NullLogger<RealtimeStateDeliveryWorker>.Instance);
        Check(await delivery.DeliverAsync(default) > 0, "durable journal delivery still drains after the authorization commit");
        await Until(() => socketObserver.Events.Any(x => x.Name == "stateChanged"));
        Check(socketAdmin1.Events.Count + socketAdmin2.Events.Count == deniedCount &&
            await initial.AuthenticateAsync(seed, adminOne.AccessToken, default) is null,
            "demoted ignoring sockets receive no later passive journal frames while an authorized administrator does");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            (await db.Users.SingleAsync(x => x.Id == other.Id)).IsActive = false;
            await db.SaveChangesAsync();
        }
        await socketOther.Closed.WaitAsync(TimeSpan.FromSeconds(3));
        Check(await initial.AuthenticateAsync(seed, otherToken.AccessToken, default) is null,
            "implicit SaveChanges transaction also commits disablement and aborts its idle socket");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await scope.ServiceProvider.GetRequiredService<AuthSessionService>().RevokeAllForUserAsync(db, first.Id, default);
            Check(!socket2.Closed.IsCompleted, "all-user raw session revocation also waits for its enclosing commit");
            await tx.CommitAsync();
        }
        await socket2.Closed.WaitAsync(TimeSpan.FromSeconds(3));
        await socketObserver.InvokeAsync("SetForeground", true);
        Check(registry.Count == 1 && !socketObserver.Closed.IsCompleted, "all-user logout closes the remaining family without disturbing another account");

        async Task<SessionTokens> NewFirstSession(string device)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>();
            return await scope.ServiceProvider.GetRequiredService<AuthSessionService>()
                .CreateAsync(db, await db.Users.SingleAsync(x => x.Id == first.Id), device, default);
        }
        async Task<RefreshedSession?> Refresh(string token)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AuthSessionService>()
                .RefreshAsync(scope.ServiceProvider.GetRequiredService<YemenDriveDbContext>(), token, default);
        }
        var replayTokens = await NewFirstSession("refresh-replay");
        await using var replaySocket = await IgnoringSignalRClient.ConnectAsync(url, replayTokens.AccessToken);
        Check(await Refresh(replayTokens.RefreshToken) is not null && !replaySocket.Closed.IsCompleted,
            "normal token rotation preserves its established family socket until access expiry");
        Check(await Refresh(replayTokens.RefreshToken) is null, "replaying an already rotated refresh credential is rejected");
        await replaySocket.Closed.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!socketObserver.Closed.IsCompleted, "refresh replay forcibly closes only its family despite an ignored notification");

        var racingTokens = await NewFirstSession("refresh-race");
        await using var racingSocket = await IgnoringSignalRClient.ConnectAsync(url, racingTokens.AccessToken);
        RefreshedSession?[] refreshed;
        refreshReadBarrier.Arm();
        try { refreshed = await Task.WhenAll(Refresh(racingTokens.RefreshToken), Refresh(racingTokens.RefreshToken)); }
        finally { refreshReadBarrier.Disarm(); }
        Check(refreshed.Count(x => x is not null) == 1,
            "two refresh requests reading the same credential have exactly one successful claim");
        await racingSocket.Closed.WaitAsync(TimeSpan.FromSeconds(3));
        Check(await initial.AuthenticateAsync(seed, refreshed.Single(x => x is not null)!.Tokens.AccessToken, default) is null,
            "losing refresh claim revokes its family and closes the idle socket at that transaction's commit");

        // Simulate a SQL revocation from a different process. This local registry
        // gets no commit callback, so the next method must still revalidate it.
        await seed.AuthSessions.Where(x => x.UserId == observer.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAtUtc, DateTime.UtcNow));
        try { await socketObserver.InvokeAsync("SetForeground", true); } catch (IOException) { }
        await socketObserver.Closed.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => registry.Count == 0);
        Check(registry.Count == 0, "hub filter rejects a family revoked externally before executing its next method");
        Console.WriteLine("PASS: session revocation enforced with isolated SQL and real uncooperative WebSocket clients.");
    }
    finally { await app.StopAsync(); }
}
finally
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^YemenDrive_SessionRevocationTests_[a-f0-9]{32}$"))
        throw new InvalidOperationException("Unsafe test database cleanup.");
    await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
    if (File.Exists(configPath)) File.Delete(configPath);
}
static void Check(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }
static async Task Until(Func<bool> ready)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!ready()) await Task.Delay(10, deadline.Token);
}
