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

if (args.Length != 1) throw new ArgumentException("Pass a safe published API template directory.");
var template = Path.GetFullPath(args[0]);
if (!template.StartsWith(@"E:\Driver\work\emulator-tracking-", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Only the isolated emulator API template is accepted.");
var id = Guid.NewGuid().ToString("N");
var name = "YemenDrive_LiveTrackingTests_" + id;
var root = Path.Combine(@"E:\Driver\work", "live-tracking-http-test-" + id);
Directory.CreateDirectory(root);
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={name};Integrated Security=True;TrustServerCertificate=True";
var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
var checks = new List<string>();
Process? process = null;
Task<string>? stdout = null, stderr = null;
try
{
    // Copy only executable/runtime inputs, never a fixture's configuration or data.
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
    await seed.Database.MigrateAsync();
    Check(!seed.Database.HasPendingModelChanges(), "UTC read conversion requires no schema migration");
    var rider = new User { PhoneNumber = "live-test-rider", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "live-test-driver", Role = UserRole.Driver };
    var other = new User { PhoneNumber = "live-test-other", Role = UserRole.Customer };
    var kind = new ServiceKind { Code = "live-test", NameAr = "اختبار معزول" };
    var catalog = new RideServiceCatalogItem { Code = "live-test-car", NameAr = "اختبار", ServiceKind = kind, Seats = 4 };
    var ride = new Ride { Customer = rider, Driver = driver, ServiceKind = kind, ServiceCatalogItem = catalog,
        Status = RideStatus.DriverAssigned, PickupLatitude = 15.3694, PickupLongitude = 44.1910,
        DestinationLatitude = 15.3754, DestinationLongitude = 44.1960, PaymentMethodCode = "cash" };
    seed.Rides.Add(ride); seed.Users.Add(other);
    seed.DriverProfiles.Add(new DriverProfile { User = driver, ServiceKind = kind, ServiceCatalogItem = catalog,
        IsAvailable = true, IsApproved = true, ApprovedPassengerSeats = 4 });
    seed.DriverLiveLocations.Add(new DriverLiveLocation { Driver = driver, Latitude = 15.3694, Longitude = 44.1910,
        ObservedAtUtc = DateTime.UtcNow.AddSeconds(-10), IsOnline = true });
    await seed.SaveChangesAsync();
    var auth = new AuthSessionService();
    var riderSession = await auth.CreateAsync(seed, rider, "test-rider", default);
    var driverSession = await auth.CreateAsync(seed, driver, "test-driver", default);
    var otherSession = await auth.CreateAsync(seed, other, "test-other", default);
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var url = $"http://127.0.0.1:{port}/";
    Directory.CreateDirectory(Path.Combine(root, "security"));
    var settings = new { ApiUrl = url, Database = new DatabaseSettings { Provider = "SqlServer", Server = @"(localdb)\MSSQLLocalDB",
            Database = name, IntegratedSecurity = true, TrustServerCertificate = true },
        Realtime = new { DurableEvents = false, DrainPushBatches = false },
        Tracking = new { DurableSamplesEnabled = false, BufferedVisualDeliveryEnabled = false },
        Firebase = new { CredentialsPath = Path.Combine(root, "disabled-fcm.json"), ProjectId = "isolated-live-test" },
        GoogleMaps = new { ApiKey = "" }, WalletLogin = new { KeyRingPath = Path.Combine(root, "wallet-keys") } };
    await File.WriteAllTextAsync(Path.Combine(root, "security", "settings.json"), JsonSerializer.Serialize(settings));
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false,
        CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Path.Combine(root, "YemenDrive.Api.dll"));
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["DOTNET_ENVIRONMENT"] = "Development";
    start.Environment["GOOGLE_APPLICATION_CREDENTIALS"] = Path.Combine(root, "disabled-fcm.json");
    process = Process.Start(start)!;
    stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
    using var anonymous = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
    for (var attempt = 0; ; attempt++)
    {
        if (process.HasExited) throw new InvalidOperationException("Isolated full API exited at startup.");
        try { using var response = await anonymous.GetAsync("hubs/tracking"); if ((int)response.StatusCode == 401) break; }
        catch (HttpRequestException) when (attempt < 60) { }
        if (attempt >= 60) throw new TimeoutException("Full API readiness timed out.");
        await Task.Delay(250);
    }
    using var riderHttp = Client(riderSession.AccessToken);
    using var driverHttp = Client(driverSession.AccessToken);
    using var otherHttp = Client(otherSession.AccessToken);
    await using var riderSocket = new HubProbe();
    await using var driverSocket = new HubProbe();
    await using var otherSocket = new HubProbe();
    var riderEvents = new ConcurrentQueue<(string Name, JsonElement Data)>();
    var driverEvents = new ConcurrentQueue<(string Name, JsonElement Data)>();
    var otherEvents = new ConcurrentQueue<(string Name, JsonElement Data)>();
    riderSocket.OnEvent = (name, data) => riderEvents.Enqueue((name, data));
    driverSocket.OnEvent = (name, data) => driverEvents.Enqueue((name, data));
    otherSocket.OnEvent = (name, data) => otherEvents.Enqueue((name, data));
    await riderSocket.ConnectAsync(url, riderSession.AccessToken);
    await driverSocket.ConnectAsync(url, driverSession.AccessToken);
    await otherSocket.ConnectAsync(url, otherSession.AccessToken);
    await riderSocket.InvokeAsync("JoinRide", ride.Id);
    var replay = await riderSocket.InvokeAsync("ReplayStateChanges", new Dictionary<string, long>(), "");
    Check(!replay.GetProperty("supported").GetBoolean(), "live tracking tests run with durable events disabled");

    var initial = DateTime.UtcNow.AddSeconds(-2);
    object DriverPoint(DateTime time, double latitude) => new { rideId = ride.Id, latitude, longitude = 44.1910,
        bearing = 30, speed = 2, accuracy = 5, observedAtUtc = time };
    object PassengerPoint(DateTime time, double latitude = 15.3694, double accuracy = 5) => new { rideId = ride.Id,
        latitude, longitude = 44.1910, bearing = 30, speed = 2, accuracy, observedAtUtc = time };
    await driverSocket.InvokeAsync("PublishDriverLocation", DriverPoint(initial, 15.36941));
    await WaitEvent(riderEvents, "driverLocationUpdated", 1);
    Check(riderEvents.Last(x => x.Name == "driverLocationUpdated").Data.GetProperty("rideStatus").GetInt32() == (int)RideStatus.DriverAssigned,
        "assigned driver GPS reaches rider before heading starts");
    await AssertSnapshot(initial, 15.36941);

    ride.Status = RideStatus.DriverEnRoute; await seed.SaveChangesAsync();
    var enroute = initial.AddSeconds(1);
    using (var response = await driverHttp.PostAsJsonAsync("api/tracking/driver-location", DriverPoint(enroute, 15.36942)))
        Check(response.IsSuccessStatusCode, "driver HTTP fallback accepts heading sample");
    await WaitEvent(riderEvents, "driverLocationUpdated", 2);
    Check(riderEvents.Last(x => x.Name == "driverLocationUpdated").Data.GetProperty("rideStatus").GetInt32() == (int)RideStatus.DriverEnRoute,
        "heading GPS is published with updated phase");
    ride.Status = RideStatus.InProgress; ride.StartedAtUtc = DateTime.UtcNow; await seed.SaveChangesAsync();
    var started = initial.AddSeconds(2);
    await driverSocket.InvokeAsync("PublishDriverLocation", DriverPoint(started, 15.36943));
    await WaitEvent(riderEvents, "driverLocationUpdated", 3);
    Check(riderEvents.Last(x => x.Name == "driverLocationUpdated").Data.GetProperty("rideStatus").GetInt32() == (int)RideStatus.InProgress,
        "started ride continues live GPS with in-progress phase");
    await AssertSnapshot(started, 15.36943);

    var passengerAt = DateTime.UtcNow.AddSeconds(-2);
    await riderSocket.InvokeAsync("PublishPassengerLocation", PassengerPoint(passengerAt));
    await WaitEvent(driverEvents, "passengerLocationUpdated", 1);
    using (var response = await riderHttp.PostAsJsonAsync("api/tracking/passenger-location", PassengerPoint(passengerAt.AddSeconds(1), 15.36941)))
        Check(response.IsSuccessStatusCode, "passenger HTTP fallback accepts the same contract as Hub");
    await WaitEvent(driverEvents, "passengerLocationUpdated", 2);
    var concurrent = PassengerPoint(passengerAt.AddSeconds(2), 15.36942);
    await Task.WhenAll(riderSocket.InvokeAsync("PublishPassengerLocation", concurrent), SendPassenger(concurrent));
    await WaitEvent(driverEvents, "passengerLocationUpdated", 3);
    Check(await seed.LocationUpdates.AsNoTracking().CountAsync(x => x.RideId == ride.Id && x.ActorId == rider.Id) == 3,
        "overlapping Hub and HTTP retry stores one passenger observation");
    var passengerSaved = await seed.LocationUpdates.AsNoTracking().Where(x => x.ActorId == rider.Id)
        .Select(x => x.ObservedAtUtc).FirstAsync();
    Check(passengerSaved.Kind == DateTimeKind.Utc, "SQL passenger GPS projection restores UTC kind");
    using (var response = await anonymous.PostAsJsonAsync("api/tracking/passenger-location", PassengerPoint(DateTime.UtcNow)))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous passenger GPS is rejected");
    using (var response = await otherHttp.PostAsJsonAsync("api/tracking/passenger-location", PassengerPoint(DateTime.UtcNow)))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "other rider cannot publish GPS for this ride");
    using (var response = await driverHttp.PostAsJsonAsync("api/tracking/passenger-location", PassengerPoint(DateTime.UtcNow)))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "driver identity cannot impersonate passenger GPS");
    using (var response = await riderHttp.PostAsJsonAsync("api/tracking/passenger-location", PassengerPoint(DateTime.UtcNow, accuracy: 200)))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "HTTP fallback preserves GPS accuracy validation");
    Check(!otherEvents.Any(x => x.Name is "driverLocationUpdated" or "passengerLocationUpdated"),
        "live coordinates are not sent to an unrelated authenticated connection");
    ride.ServiceEndedAtUtc = DateTime.UtcNow; ride.Status = RideStatus.Completed; await seed.SaveChangesAsync();
    using (var response = await riderHttp.PostAsJsonAsync("api/tracking/passenger-location", PassengerPoint(DateTime.UtcNow)))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "completed ride cannot accept later passenger coordinates");
    await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, checks, testRoot = root }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS: {checks.Count} full API live tracking assertions. Evidence: {Path.Combine(root, "result.json")}");

    HttpClient Client(string token) { var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return client; }
    async Task SendPassenger(object payload) { using var response = await riderHttp.PostAsJsonAsync("api/tracking/passenger-location", payload); response.EnsureSuccessStatusCode(); }
    async Task AssertSnapshot(DateTime observed, double latitude)
    {
        using var response = await riderHttp.PostAsJsonAsync("api/execute", new { model = "RideModel", operation = "get", data = new { id = ride.Id } });
        response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var point = json.RootElement.GetProperty("data").GetProperty("driverLocation");
        Check(point.GetProperty("latitude").GetDouble() == latitude && point.GetProperty("observedAtUtc").GetString()!.EndsWith("Z") &&
            point.GetProperty("observedAtUtc").GetDateTime() == observed, "canonical REST GPS has latest coordinates and explicit UTC timestamp");
    }
}
finally
{
    if (process is not null)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        if (stdout is not null) await File.WriteAllTextAsync(Path.Combine(root, "stdout.log"), await stdout);
        if (stderr is not null) await File.WriteAllTextAsync(Path.Combine(root, "stderr.log"), await stderr);
        process.Dispose();
    }
    if (name != "YemenDrive_LiveTrackingTests_" + id) throw new InvalidOperationException("Unsafe cleanup database.");
    await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
}

void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks.Add(description); Console.WriteLine("PASS: " + description); }
static async Task WaitEvent(ConcurrentQueue<(string Name, JsonElement Data)> events, string name, int count)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (events.Count(x => x.Name == name) < count) { if (DateTime.UtcNow > deadline) throw new TimeoutException($"Missing {name} event {count}"); await Task.Delay(20); }
}
