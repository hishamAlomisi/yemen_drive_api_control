using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Api;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.RealtimeBenchmarks;

internal static class MovementHttpScenarios
{
    internal static async Task RunAsync(DbContextOptions<YemenDriveDbContext> options, DatabaseConfigurationStore config, Action<bool, string> check)
    {
        var root = Path.Combine(@"E:\Driver\work\driver-movement-20261105", "http-" + Guid.NewGuid().ToString("N"));
        var template = @"E:\Driver\api\src\YemenDrive.Api\bin\Release\net9.0";
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(template, file);
            if (!(Path.GetDirectoryName(relative) == "" && (file.EndsWith(".dll") || file.EndsWith(".exe") || file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))) && !relative.StartsWith("runtimes" + Path.DirectorySeparatorChar)) continue;
            var destination = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        await using var db = new YemenDriveDbContext(options);
        var driver = await db.Users.SingleAsync(x => x.PhoneNumber == "movement-admin-driver");
        var rider = await db.Users.SingleAsync(x => x.PhoneNumber == "movement-admin-rider");
        var admin = await db.Users.SingleAsync(x => x.PhoneNumber == "movement-admin-system");
        var limited = await db.Users.SingleAsync(x => x.PhoneNumber == "movement-admin-limited");
        var ride = await db.Rides.SingleAsync(x => x.DriverId == driver.Id);
        ride.Status = RideStatus.InProgress; ride.ServiceEndedAtUtc = null; ride.StartedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var auth = new AuthSessionService();
        var riderSession = await auth.CreateAsync(db, rider, "movement-http-rider", default);
        var adminSession = await auth.CreateAsync(db, admin, "movement-http-admin", default);
        var limitedSession = await auth.CreateAsync(db, limited, "movement-http-limited", default);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = $"http://127.0.0.1:{port}/";
        Directory.CreateDirectory(Path.Combine(root, "security"));
        await File.WriteAllTextAsync(Path.Combine(root, "security", "settings.json"), JsonSerializer.Serialize(new
        {
            ApiUrl = url, Database = config.Current, Realtime = new { DurableEvents = true, DrainPushBatches = false },
            Tracking = new { DurableSamplesEnabled = false, BufferedVisualDeliveryEnabled = false },
            Firebase = new { CredentialsPath = Path.Combine(root, "disabled-fcm.json"), ProjectId = "movement-isolated" },
            WalletLogin = new { KeyRingPath = Path.Combine(root, "wallet-keys") }
        }));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "YemenDrive.Api.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["GOOGLE_APPLICATION_CREDENTIALS"] = Path.Combine(root, "disabled-fcm.json");
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var anonymous = new HttpClient { BaseAddress = new(url), Timeout = TimeSpan.FromSeconds(10) };
            for (var attempt = 0; ; attempt++)
            {
                if (process.HasExited) throw new Exception("Isolated API exited: " + root);
                try { using var response = await anonymous.GetAsync("hubs/tracking"); if (response.StatusCode == HttpStatusCode.Unauthorized) break; }
                catch (HttpRequestException) when (attempt < 80) { }
                if (attempt >= 80) throw new TimeoutException("Isolated API startup");
                await Task.Delay(250);
            }
            using var adminHttp = Client(adminSession.AccessToken); using var riderHttp = Client(riderSession.AccessToken); using var limitedHttp = Client(limitedSession.AccessToken);
            await using var socket = new HubProbe(); var events = new ConcurrentQueue<(string Name, JsonElement Data)>();
            socket.OnEvent = (name, data) => events.Enqueue((name, data));
            await socket.ConnectAsync(url, riderSession.AccessToken); await socket.InvokeAsync("JoinRide", ride.Id);
            await Execute(adminHttp, "DriverLocationAdminModel", "save", new { driverId = driver.Id, latitude = 15.36, longitude = 44.19 });
            await Until(() => events.Any(x => x.Name == "driverLocationUpdated"));
            var before = events.Count(x => x.Name == "driverLocationUpdated");
            var moved = await Execute(adminHttp, "DriverLocationAdminModel", "move", new { driverId = driver.Id });
            await Until(() => events.Count(x => x.Name == "driverLocationUpdated") > before);
            check(moved.GetProperty("distanceMeters").GetDouble() == 1, "full HTTP admin move advances one metre and rider receives real SignalR event");
            var snapshot = await Execute(adminHttp, "DriverLocationAdminModel", "get", new { driverId = driver.Id });
            check(snapshot.GetProperty("location").GetProperty("latitude").GetDouble() > 15.36, "full API retrieves persisted administrator position");
            var report = await Execute(adminHttp, "DriverMovementReportModel", "list", new { driverId = driver.Id });
            check(report.GetProperty("rows").EnumerateArray().Any(x => x.GetProperty("rideId").GetInt32() == ride.Id), "full API registers movement report contract");
            var history = await Execute(adminHttp, "RideMovementHistoryModel", "get", new { rideId = ride.Id });
            check(history.GetProperty("tripSamples").GetArrayLength() > 0 && history.GetProperty("hasSimulatedSamples").GetBoolean(), "full API historical map returns saved simulated points and source label");
            await Execute(limitedHttp, "DriverLocationAdminModel", "save", new { driverId = driver.Id, latitude = 15, longitude = 44 }, "administrator_permission_required");
            await Execute(riderHttp, "DriverLocationAdminModel", "move", new { driverId = driver.Id }, "administrator_required", false);
            await Execute(riderHttp, "RideMovementHistoryModel", "get", new { rideId = ride.Id }, "administrator_required", false);
            check(true, "HTTP boundary denies unprivileged admin and rider mutations/history");
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, adminHttpSaveAndMove = true, riderSignalR = true, reportHistory = true, authorization = true }));
            Console.WriteLine("Movement HTTP/SignalR evidence: " + Path.Combine(root, "result.json"));
            HttpClient Client(string token) { var client = new HttpClient { BaseAddress = new(url), Timeout = TimeSpan.FromSeconds(20) }; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return client; }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await File.WriteAllTextAsync(Path.Combine(root, "stdout.log"), await stdout); await File.WriteAllTextAsync(Path.Combine(root, "stderr.log"), await stderr);
        }
    }
    private static async Task<JsonElement> Execute(HttpClient client, string model, string operation, object data, string? denied = null, bool adminRoute = true)
    {
        using var response = await client.PostAsJsonAsync(adminRoute ? "api/admin/execute" : "api/execute", new { model, operation, data }); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (denied != null) { if (json.RootElement.GetProperty("success").GetBoolean() || json.RootElement.GetProperty("code").GetString() != denied) throw new Exception("Unexpected denial " + model); return default; }
        if (!json.RootElement.GetProperty("success").GetBoolean()) throw new Exception(model + ": " + json.RootElement.GetProperty("code").GetString());
        return json.RootElement.GetProperty("data").Clone();
    }
    private static async Task Until(Func<bool> predicate) { for (var i = 0; i < 120; i++) { if (predicate()) return; await Task.Delay(100); } throw new TimeoutException("Location event not received"); }
}
