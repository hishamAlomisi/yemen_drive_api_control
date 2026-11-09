using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Api;
using YemenDrive.Api.Safety;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.RealtimeBenchmarks;

internal static class RouteSafetyHttpChecks
{
    internal static async Task RunAsync(DbContextOptions<YemenDriveDbContext> options,
        DatabaseConfigurationStore config, int rideId, int customerId, int driverId, int outsiderId,
        string route, Action<bool, string> check)
    {
        var root = Path.Combine(@"E:\Driver\work\trip-experience-20261105", "safety-http-" + Guid.NewGuid().ToString("N"));
        var template = Path.GetDirectoryName(typeof(RouteDeviationService).Assembly.Location)!;
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(template, file);
            if (!(Path.GetDirectoryName(relative) == "" && (file.EndsWith(".dll") || file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))) &&
                !relative.StartsWith("runtimes" + Path.DirectorySeparatorChar)) continue;
            var destination = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        await using var db = new YemenDriveDbContext(options);
        await db.Rides.Where(x => x.Id == rideId).ExecuteUpdateAsync(x => x.SetProperty(x => x.Status, RideStatus.InProgress)
            .SetProperty(x => x.ServiceEndedAtUtc, (DateTime?)null).SetProperty(x => x.RoutePolyline, route)
            .SetProperty(x => x.RouteVersion, 5).SetProperty(x => x.RouteSegmentStartedAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        db.LocationUpdates.Add(new() { RideId = rideId, ActorId = driverId, Latitude = .0005, Longitude = .005,
            Accuracy = 3, Source = "gps", ObservedAtUtc = DateTime.UtcNow }); await db.SaveChangesAsync();
        var sessions = new AuthSessionService();
        var riderToken = (await sessions.CreateAsync(db, await db.Users.SingleAsync(x => x.Id == customerId), "safety-http-rider", default)).AccessToken;
        var driverToken = (await sessions.CreateAsync(db, await db.Users.SingleAsync(x => x.Id == driverId), "safety-http-driver", default)).AccessToken;
        var outsiderToken = (await sessions.CreateAsync(db, await db.Users.SingleAsync(x => x.Id == outsiderId), "safety-http-outsider", default)).AccessToken;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = $"http://127.0.0.1:{port}/";
        Directory.CreateDirectory(Path.Combine(root, "security"));
        await File.WriteAllTextAsync(Path.Combine(root, "security", "settings.json"), JsonSerializer.Serialize(new
        {
            ApiUrl = url, Database = config.Current, Realtime = new { DurableEvents = true, DrainPushBatches = false },
            Tracking = new { DurableSamplesEnabled = false, BufferedVisualDeliveryEnabled = false },
            Firebase = new { CredentialsPath = Path.Combine(root, "disabled-fcm.json"), ProjectId = "safety-isolated" },
            WalletLogin = new { KeyRingPath = Path.Combine(root, "wallet-keys") }
        }));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "YemenDrive.Api.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["GOOGLE_APPLICATION_CREDENTIALS"] = Path.Combine(root, "disabled-fcm.json");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var anonymous = new HttpClient { BaseAddress = new(url), Timeout = TimeSpan.FromSeconds(10) };
            for (var attempt = 0; ; attempt++)
            {
                if (process.HasExited) throw new Exception("Isolated safety API exited: " + root);
                try { using var response = await anonymous.GetAsync("hubs/tracking"); if (response.StatusCode == HttpStatusCode.Unauthorized) break; }
                catch (HttpRequestException) when (attempt < 80) { }
                if (attempt >= 80) throw new TimeoutException("Isolated API startup");
                await Task.Delay(250);
            }
            using var rider = Client(riderToken); using var outsider = Client(outsiderToken);
            var path = $"api/rides/{rideId}/route-deviation";
            using var denied = await anonymous.GetAsync(path);
            check(denied.StatusCode == HttpStatusCode.Unauthorized, "HTTP deviation endpoint requires authenticated session");
            using var forbidden = await outsider.PostAsJsonAsync(path + "/report", new { });
            check(forbidden.StatusCode == HttpStatusCode.Forbidden, "HTTP report denies another rider");
            using var status = await rider.GetAsync(path); var statusJson = await status.Content.ReadFromJsonAsync<JsonElement>();
            check(status.IsSuccessStatusCode && statusJson.GetProperty("data").GetProperty("isDeviating").GetBoolean() &&
                status.Headers.CacheControl?.NoStore == true, "HTTP authorized status returns fresh deviation and no-store cache policy");
            await using var socket = new HubProbe(); var events = new ConcurrentQueue<(string Name, JsonElement Data)>();
            socket.OnEvent = (name, data) => events.Enqueue((name, data));
            await socket.ConnectAsync(url, driverToken);
            using var submitted = await rider.PostAsJsonAsync(path + "/report", new { latitude = 0, longitude = 0, distanceMeters = 0 });
            var report = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            check(submitted.IsSuccessStatusCode && report.GetProperty("accepted").GetBoolean() &&
                report.GetProperty("distanceMeters").GetDouble() > 50, "HTTP report uses server GPS and ignores supplied location/distance spoofing");
            await Until(() => events.Any(x => x.Name == "notificationCreated" && x.Data.GetProperty("notifications")
                .EnumerateArray().Any(item => item.TryGetProperty("action", out var action) && action.GetString() == "route_deviation_reported")));
            check(true, "driver receives real SignalR safety notification with route-deviation action");
            var incidentId = report.GetProperty("incidentId").GetInt32();
            check(await db.NotificationPushOutbox.AnyAsync(x => x.NotificationId == db.Notifications
                .Where(n => n.UserId == driverId && n.DataJson!.Contains("\"safetyIncidentId\":" + incidentId))
                .Select(n => n.Id).FirstOrDefault()), "driver push notification is durably queued by normal delivery pipeline");
            db.LocationUpdates.Add(new() { RideId = rideId, ActorId = driverId, Latitude = 0, Longitude = .005,
                Accuracy = 3, Source = "gps", ObservedAtUtc = DateTime.UtcNow }); await db.SaveChangesAsync();
            using var rejected = await rider.PostAsJsonAsync(path + "/report", new { distanceMeters = 1000 });
            var rejection = (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            check(!rejection.GetProperty("accepted").GetBoolean() && rejection.GetProperty("reasonCode").GetString() == "on_route",
                "HTTP manual action returns friendly rejection after real driver returns to route");
            check((await db.Rides.AsNoTracking().SingleAsync(x => x.Id == rideId)).Status == RideStatus.InProgress,
                "HTTP safety flow leaves live ride and payment state untouched");
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true,
                authenticatedHttp = true, serverRevalidation = true, driverSignalR = true, durablePush = true }));
            Console.WriteLine("Safety HTTP evidence: " + Path.Combine(root, "result.json"));
            HttpClient Client(string token) { var value = new HttpClient { BaseAddress = new(url), Timeout = TimeSpan.FromSeconds(15) };
                value.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return value; }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await File.WriteAllTextAsync(Path.Combine(root, "stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(root, "stderr.log"), await stderr);
        }
    }
    private static async Task Until(Func<bool> ready)
    { var deadline = DateTime.UtcNow.AddSeconds(10); while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!ready()) throw new TimeoutException("Driver safety SignalR event did not arrive."); }
}
