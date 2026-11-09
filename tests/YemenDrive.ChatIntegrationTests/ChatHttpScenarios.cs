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

internal static class ChatHttpScenarios
{
    internal static async Task RunAsync(DbContextOptions<YemenDriveDbContext> options, string databaseName,
        User rider, User driver, User outsider, User admin, User limitedAdmin, int rideId, Action<bool, string> check)
    {
        var root = Path.Combine(@"E:\Driver\work", "chat-http-test-" + Guid.NewGuid().ToString("N"));
        var template = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/YemenDrive.Api/bin/Release/net9.0"));
        if (!Directory.Exists(template)) template = @"E:\Driver\api\src\YemenDrive.Api\bin\Release\net9.0";
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(template, file);
            if (!(Path.GetDirectoryName(relative) == "" &&
                (file.EndsWith(".dll") || file.EndsWith(".exe") || file.EndsWith(".deps.json") || file.EndsWith(".runtimeconfig.json"))) &&
                !relative.StartsWith("runtimes" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        await using var seed = new YemenDriveDbContext(options);
        var auth = new AuthSessionService();
        var riderToken = await auth.CreateAsync(seed, rider, "http-chat-rider", default);
        var driverToken = await auth.CreateAsync(seed, driver, "http-chat-driver", default);
        var outsiderToken = await auth.CreateAsync(seed, outsider, "http-chat-other", default);
        var adminToken = await auth.CreateAsync(seed, admin, "http-chat-admin", default);
        var limitedToken = await auth.CreateAsync(seed, limitedAdmin, "http-chat-limited", default);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = $"http://127.0.0.1:{port}/";
        Directory.CreateDirectory(Path.Combine(root, "security"));
        var settings = new
        {
            ApiUrl = url,
            Database = new DatabaseSettings { Provider = "SqlServer", Server = @"(localdb)\MSSQLLocalDB", Database = databaseName, IntegratedSecurity = true, TrustServerCertificate = true },
            Realtime = new { DurableEvents = true, DrainPushBatches = false },
            Tracking = new { DurableSamplesEnabled = false, BufferedVisualDeliveryEnabled = false },
            Firebase = new { CredentialsPath = Path.Combine(root, "disabled-fcm.json"), ProjectId = "isolated-chat-test" },
            GoogleMaps = new { ApiKey = "" }, WalletLogin = new { KeyRingPath = Path.Combine(root, "wallet-keys") }
        };
        await File.WriteAllTextAsync(Path.Combine(root, "security", "settings.json"), JsonSerializer.Serialize(settings));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "YemenDrive.Api.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["GOOGLE_APPLICATION_CREDENTIALS"] = Path.Combine(root, "disabled-fcm.json");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var anonymous = new HttpClient { BaseAddress = new(url), Timeout = TimeSpan.FromSeconds(10) };
            for (var attempt = 0; ; attempt++)
            {
                if (process.HasExited) throw new InvalidOperationException("Isolated API exited. Logs: " + root);
                try { using var response = await anonymous.GetAsync("hubs/tracking"); if (response.StatusCode == HttpStatusCode.Unauthorized) break; }
                catch (HttpRequestException) when (attempt < 80) { }
                if (attempt >= 80) throw new TimeoutException("Isolated API readiness timeout.");
                await Task.Delay(250);
            }
            using var riderHttp = Client(riderToken.AccessToken);
            using var driverHttp = Client(driverToken.AccessToken);
            using var outsiderHttp = Client(outsiderToken.AccessToken);
            using var adminHttp = Client(adminToken.AccessToken);
            using var limitedHttp = Client(limitedToken.AccessToken);
            await using var riderSocket = new HubProbe();
            await using var driverSocket = new HubProbe();
            await using var outsiderSocket = new HubProbe();
            var riderEvents = new ConcurrentQueue<(string Name, JsonElement Data)>();
            var driverEvents = new ConcurrentQueue<(string Name, JsonElement Data)>();
            var outsiderEvents = new ConcurrentQueue<(string Name, JsonElement Data)>();
            riderSocket.OnEvent = (name, data) => riderEvents.Enqueue((name, data));
            driverSocket.OnEvent = (name, data) => driverEvents.Enqueue((name, data));
            outsiderSocket.OnEvent = (name, data) => outsiderEvents.Enqueue((name, data));
            await riderSocket.ConnectAsync(url, riderToken.AccessToken);
            await driverSocket.ConnectAsync(url, driverToken.AccessToken);
            await outsiderSocket.ConnectAsync(url, outsiderToken.AccessToken);
            // No JoinRide: open chats and outside-chat badges must both work from
            // the authenticated user subscription even after ride group churn.
            var replay = await driverSocket.InvokeAsync("ReplayStateChanges", new Dictionary<string, long>(), "");
            await riderSocket.InvokeAsync("ReplayStateChanges", new Dictionary<string, long>(), "");
            await outsiderSocket.InvokeAsync("ReplayStateChanges", new Dictionary<string, long>(), "");
            check(replay.GetProperty("supported").GetBoolean(), "full API uses durable realtime recovery");
            var sent = await Execute(riderHttp, "RideMessageModel", "add", new { rideId, content = "HTTP and WebSocket live message" });
            var messageId = sent.GetProperty("id").GetInt32();
            await Until(() => Has(riderEvents, "rideMessageCreated", messageId) && Has(driverEvents, "rideMessageCreated", messageId));
            check(true, "real authenticated sockets receive message immediately without JoinRide");
            check(sent.GetProperty("createdAtUtc").GetString()!.EndsWith('Z'), "HTTP message timestamp is explicit UTC");
            var summary = await Execute(driverHttp, "RideMessageModel", "report", new { rideId });
            check(summary.GetProperty("unreadCount").GetInt32() == 1, "authenticated HTTP unread count reflects live incoming message");
            var delivered = await Execute(driverHttp, "RideMessageModel", "update", new { id = messageId, markDelivered = true, markRead = false });
            await Until(() => HasReceipt(riderEvents, messageId, false) && HasReceipt(driverEvents, messageId, false));
            check(delivered.GetProperty("readAtUtc").ValueKind == JsonValueKind.Null, "both real sockets receive delivery receipt while message stays unread");
            await Execute(driverHttp, "RideMessageModel", "update", new { id = messageId, markRead = true });
            await Until(() => HasReceipt(riderEvents, messageId, true) && HasReceipt(driverEvents, messageId, true));
            check(true, "both real sockets receive read receipt immediately");
            summary = await Execute(driverHttp, "RideMessageModel", "report", new { rideId });
            check(summary.GetProperty("unreadCount").GetInt32() == 0, "HTTP unread count clears after read receipt");
            await Until(() => driverEvents.Any(item => item.Name == "stateChanged" && item.Data.GetProperty("resource").GetString() == "messages" && item.Data.GetProperty("resourceId").GetInt32() == rideId));
            check(true, "durable message invalidations also reach the authenticated socket");
            check(!outsiderEvents.Any(item => item.Name is "rideMessageCreated" or "rideMessageUpdated"), "unrelated authenticated socket receives no chat content or receipts");
            var drivers = await Execute(adminHttp, "RideChatAuditModel", "list", new { }, adminRoute: true);
            check(drivers.EnumerateArray().Any(item => item.GetProperty("id").GetInt32() == driver.Id), "full admin dispatcher registers and authorizes chat audit selector");
            var transcript = await Execute(adminHttp, "RideChatAuditModel", "get", new { driverId = driver.Id, customerId = rider.Id, rideId }, adminRoute: true);
            check(transcript.GetProperty("messages").EnumerateArray().Any(item => item.GetProperty("id").GetInt32() == messageId), "authorized admin HTTP transcript includes new message");
            await Execute(limitedHttp, "RideChatAuditModel", "list", new { }, adminRoute: true, expectedCode: "administrator_permission_required");
            await Execute(outsiderHttp, "RideChatAuditModel", "list", new { }, expectedCode: "administrator_required");
            await Execute(outsiderHttp, "RideMessageModel", "list", new { rideId }, expectedCode: "ride_access_denied");
            check(true, "HTTP authorizer rejects unauthorized admin and unrelated rider requests");
            await Execute(anonymous, "RideMessageModel", "report", new { rideId }, expectedCode: "authentication_required");
            check(true, "anonymous HTTP chat access is rejected by the model authentication contract");
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, durableEvents = true, joinRideCalled = false, liveMessageAndReceiptDelivery = true, adminDispatchVerified = true }));
            Console.WriteLine("HTTP/SignalR evidence: " + Path.Combine(root, "result.json"));

            HttpClient Client(string token)
            {
                var client = new HttpClient { BaseAddress = new(url), Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return client;
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(root, "stderr.log"), await stderr);
        }
    }

    private static async Task<JsonElement> Execute(HttpClient http, string model, string operation, object data,
        bool adminRoute = false, string? expectedCode = null)
    {
        using var response = await http.PostAsJsonAsync(adminRoute ? "api/admin/execute" : "api/execute", new { model, operation, data });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (expectedCode is not null)
        {
            if (json.RootElement.GetProperty("success").GetBoolean() || json.RootElement.GetProperty("code").GetString() != expectedCode)
                throw new Exception("Unexpected denial for " + model + "." + operation + ": " + json.RootElement.GetProperty("code").GetString());
            return default;
        }
        if (!json.RootElement.GetProperty("success").GetBoolean()) throw new Exception(model + "." + operation + ": " + json.RootElement.GetProperty("code").GetString());
        return json.RootElement.GetProperty("data").Clone();
    }
    private static bool Has(ConcurrentQueue<(string Name, JsonElement Data)> events, string name, int id) =>
        events.Any(item => item.Name == name && item.Data.GetProperty("id").GetInt32() == id);
    private static bool HasReceipt(ConcurrentQueue<(string Name, JsonElement Data)> events, int id, bool read) =>
        events.Any(item => item.Name == "rideMessageUpdated" && item.Data.GetProperty("id").GetInt32() == id &&
            item.Data.GetProperty(read ? "readAtUtc" : "deliveredAtUtc").ValueKind == JsonValueKind.String);
    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 120; attempt++) { if (condition()) return; await Task.Delay(100); }
        throw new TimeoutException("Expected live chat event did not arrive.");
    }
}
