using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YemenDrive.Api;
using YemenDrive.Api.Realtime;
using YemenDrive.Api.Tracking;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Rides;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Database.Realtime;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using YemenDrive.RealtimeBenchmarks;
using RideService = YemenDrive.Application.Rides.Ride;
using Ride = YemenDrive.Database.Entities.Ride;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
if (args.Length < 3) throw new ArgumentException("serve <private-ready.json> <pairs> | load <private-ready.json> <seconds> <result.json> [pairs]");
if (args[0] == "serve") await ServeAsync(Path.GetFullPath(args[1]), int.Parse(args[2]), args.Contains("--journal"), args.Contains("--buffered-visual"), args.Contains("--durable-samples"));
else if (args[0] == "load") await LoadAsync(Path.GetFullPath(args[1]), int.Parse(args[2]), Path.GetFullPath(args[3]), args.Length > 4 ? int.Parse(args[4]) : null, args.Contains("--mixed"));
else throw new ArgumentException("Unknown benchmark mode.");

async Task ServeAsync(string readyFile, int pairs, bool journal, bool bufferedVisual, bool durableSamples)
{
    if (pairs is < 1 or > 4000) throw new ArgumentOutOfRangeException(nameof(pairs));
    var databaseName = "YemenDrive_RealtimeBenchmark_" + Guid.NewGuid().ToString("N");
    var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True;Max Pool Size=100";
    var options = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
    var settingsFile = Path.Combine(Path.GetTempPath(), databaseName + ".json");
    var config = new DatabaseConfigurationStore(settingsFile);
    await config.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = databaseName, IntegratedSecurity = true, TrustServerCertificate = true }, default);
    try {
        await using var seed = new YemenDriveDbContext(options);
        await seed.Database.MigrateAsync();
        var kind = new ServiceKind { Code = "bench", NameAr = "Benchmark only" };
        var catalog = new RideServiceCatalogItem { Code = "bench-car", NameAr = "Benchmark", ServiceKind = kind, Seats = 4 };
        seed.ServiceCatalogItems.Add(catalog);
        var entries = new List<(User Driver, User Customer, Ride Ride, string DriverToken, string CustomerToken)>();
        var admin = new User { PhoneNumber = "benchmark-admin", Role = UserRole.Admin, IsSystemAdministrator = true };
        seed.Users.Add(admin);
        var adminToken = Token();
        seed.AuthSessions.Add(Session(admin, adminToken));
        for (var i = 0; i < pairs; i++) {
            var driver = new User { PhoneNumber = $"benchmark-d-{i}", Role = UserRole.Driver, DisplayName = $"Driver {i}" };
            var customer = new User { PhoneNumber = $"benchmark-c-{i}", Role = UserRole.Customer, DisplayName = $"Customer {i}" };
            seed.DriverProfiles.Add(new DriverProfile { User = driver, ServiceKind = kind, ServiceCatalogItem = catalog, IsApproved = true, IsAvailable = true, ApprovedPassengerSeats = 4 });
            seed.Wallets.AddRange(new Wallet { User = driver }, new Wallet { User = customer, Balance = 100000 });
            var ride = new Ride { Customer = customer, Driver = driver, ServiceKind = kind, ServiceCatalogItem = catalog, Status = RideStatus.InProgress,
                PickupLatitude = 15.3694, PickupLongitude = 44.1910, DestinationLatitude = 15.4, DestinationLongitude = 44.2,
                CustomerPrice = 1000, TotalAmount = 1000, PaymentMethodCode = "cash", PassengerBoardedAtUtc = DateTime.UtcNow,
                StartedAtUtc = durableSamples ? DateTime.UtcNow : null,
                PickupLabel = "Benchmark pickup", DestinationLabel = "Benchmark destination" };
            seed.Rides.Add(ride);
            seed.DriverLiveLocations.Add(new DriverLiveLocation { Driver = driver, Latitude = 15.3694, Longitude = 44.1910, IsOnline = true, ObservedAtUtc = DateTime.UtcNow });
            var driverToken = Token(); var customerToken = Token();
            seed.AuthSessions.AddRange(Session(driver, driverToken), Session(customer, customerToken));
            entries.Add((driver, customer, ride, driverToken, customerToken));
        }
        await seed.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.GetTempPath(), ApplicationName = typeof(TrackingHub).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(config);
        var commands = new CommandCounter();
        builder.Services.AddSingleton(new RealtimeJournalSettings { Enabled = journal });
        builder.Services.AddSingleton<RealtimeCommitSignal>();
        builder.Services.AddSingleton<RealtimeCommitInterceptor>();
        builder.Services.AddScoped<RealtimeStateRecovery>();
        builder.Services.AddHostedService<RealtimeStateDeliveryWorker>();
        builder.Services.AddDbContext<YemenDriveDbContext>((sp, o) => o.UseSqlServer(connection)
            .AddTrackingSessionRevocation(sp).AddInterceptors(commands, sp.GetRequiredService<RealtimeCommitInterceptor>()));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
        builder.Services.AddScoped<AuthSessionService>();
        builder.Services.AddScoped<AccountingPostingService>();
        builder.Services.AddScoped<FinancialAccountProvisioningService>();
        builder.Services.AddScoped<RideAccountingPostingService>();
        builder.Services.AddScoped<RideService>();
        builder.Services.AddScoped<RideReport>();
        builder.Services.AddScoped<TrackingHubAccessPolicy>();
        builder.Services.AddScoped<DriverLocationIngestionService>();
        builder.Services.AddScoped<IRideRealtimePublisher, SignalRRideRealtimePublisher>();
        builder.Services.AddSingleton<NearbyDriverSubscriptionStore>();
        builder.Services.AddSingleton<RideParticipantPresence>();
        builder.Configuration["Tracking:BufferedVisualDeliveryEnabled"] = bufferedVisual.ToString();
        builder.Configuration["Tracking:DurableSamplesEnabled"] = durableSamples.ToString();
        builder.Services.AddTrackingUpgrade(builder.Configuration);
        builder.Services.AddTrackingSessionRevocation();
        await using var app = builder.Build();
        app.UseMiddleware<AuthSessionAuthenticationMiddleware>();
        app.MapHub<TrackingHub>("/hubs/tracking", hubOptions => hubOptions.CloseOnAuthenticationExpiration = true);
        app.MapHub<SharedRideHub>("/hubs/shared-rides", hubOptions => hubOptions.CloseOnAuthenticationExpiration = true);
        app.MapTrackingUpgrade();
        app.MapPost("/api/execute", async (ApiRequest request, RideService rides, RideReport reports, CancellationToken token) =>
            Results.Ok(await (request.Operation == "list" ? reports.ExecuteAsync(request.Operation, request.Data, token) : rides.ExecuteAsync(request.Operation, request.Data, token))));
        app.MapGet("/benchmark/metrics", async (DriverLocationVisualQueue queue) => {
            // Isolated diagnostics use an uninstrumented context: these four
            // bookkeeping queries are not included in workload SQL counts.
            await using var diagnostics = new YemenDriveDbContext(options);
            var routeSamples = durableSamples ? await diagnostics.DriverRouteSamples.LongCountAsync() : 0;
            var routeSessions = durableSamples ? await diagnostics.DriverRouteSessions.LongCountAsync() : 0;
            var routeSessionsWithGaps = durableSamples ? await diagnostics.DriverRouteSessions
                .Where(x => x.HighestSequence != x.LastContiguousSequence).LongCountAsync() : 0;
            var routeArchive = await diagnostics.LocationUpdates.LongCountAsync();
            return new {
                commands = Interlocked.Read(ref commands.Count), commandFailures = commands.Failures,
                commandFamilies = commands.Families, commandElapsedTicks = commands.ElapsedTicks,
                workingSet = Environment.WorkingSet, cpuMs = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds,
                flags = new { durableEvents = journal, bufferedVisualDelivery = bufferedVisual, durableSamples },
                routeSamples, routeSessions, routeSessionsWithGaps, routeArchive,
                visualQueue = queue.Metrics };
        });
        app.MapPost("/benchmark/stop", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.Ok(); });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + '/';
        Directory.CreateDirectory(Path.GetDirectoryName(readyFile)!);
        await File.WriteAllTextAsync(readyFile, JsonSerializer.Serialize(new Ready(url, adminToken, entries.Select(x => new Pair(x.Ride.Id, x.Driver.Id, x.DriverToken, x.CustomerToken)).ToArray(), databaseName), jsonOptions));
        Console.WriteLine($"READY: isolated benchmark host, {pairs} driver/customer pairs. Session tokens are in the private ready file only.");
        await app.WaitForShutdownAsync();
    }
    finally {
        // Name originates here, never from CLI or a user's configured database.
        if (!databaseName.StartsWith("YemenDrive_RealtimeBenchmark_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup database.");
        await using var cleanup = new YemenDriveDbContext(options); await cleanup.Database.EnsureDeletedAsync();
        if (File.Exists(settingsFile)) File.Delete(settingsFile);
        if (File.Exists(readyFile)) File.Delete(readyFile);
    }
}

async Task LoadAsync(string readyFile, int seconds, string resultFile, int? maximumPairs, bool mixed)
{
    if (seconds is < 5 or > 14400) throw new ArgumentOutOfRangeException(nameof(seconds));
    var ready = JsonSerializer.Deserialize<Ready>(await File.ReadAllTextAsync(readyFile), jsonOptions)!;
    if (!new Uri(ready.Url).IsLoopback || !ready.Database.StartsWith("YemenDrive_RealtimeBenchmark_")) throw new InvalidOperationException("Benchmark accepts only its isolated loopback host.");
    var pairs = ready.Pairs.Take(maximumPairs ?? ready.Pairs.Length).ToArray();
    var metrics = new ConcurrentDictionary<string, ConcurrentQueue<double>>();
    long failures = 0; long gpsReceived = 0; long outOfOrder = 0; long expectedGps = 0;
    long stateReceived = 0, legacyStateReceived = 0, mutations = 0, recovered = 0, reconnects = 0;
    long routeGenerated = 0, routeAcknowledged = 0, routeRetries = 0, routeDuplicateChecks = 0;
    long routeHttpAttempts = 0, routeHubAttempts = 0, sampledFailures = 0;
    var failureTypes = new ConcurrentDictionary<string, int>();
    var failureSamples = new ConcurrentQueue<object>();
    var pendingRouteSamples = new ConcurrentDictionary<int, PendingRouteSample>();
    var peers = new List<HubProbe>();
    using var http = new HttpClient { BaseAddress = new Uri(ready.Url), Timeout = TimeSpan.FromSeconds(30) };
    var before = await http.GetFromJsonAsync<JsonElement>("benchmark/metrics");
    var durableSamples = before.TryGetProperty("flags", out var initialFlags) &&
        initialFlags.TryGetProperty("durableSamples", out var durableFlag) && durableFlag.GetBoolean();
    var durableEvents = initialFlags.GetProperty("durableEvents").GetBoolean();
    var routeSessionIds = pairs.Select(_ => Guid.NewGuid()).ToArray();
    var setup = Stopwatch.StartNew();
    var observed = new ConcurrentDictionary<int, DateTime>();
    var acknowledged = new ConcurrentDictionary<int, DateTime>();
    var expectedPaymentMethods = new ConcurrentDictionary<int, string>();
    var observedPaymentMethods = new ConcurrentDictionary<int, string>();
    void Add(string name, double value) => metrics.GetOrAdd(name, _ => new()).Enqueue(value);
    void Event(string name, JsonElement payload) {
        if (name != "driverLocationUpdated") return;
        Interlocked.Increment(ref gpsReceived);
        var at = payload.GetProperty("observedAtUtc").GetDateTime().ToUniversalTime();
        Add("gpsCaptureToClientMs", (DateTime.UtcNow - at).TotalMilliseconds);
        // One customer per ride is used for ordering, not the duplicate admin copy.
    }
    try {
        foreach (var pair in pairs) {
            var driver = new HubProbe(); var customer = new HubProbe { OnEvent = (name, payload) => {
                Event(name, payload);
                if (name == "rideStatusUpdated") {
                    Interlocked.Increment(ref legacyStateReceived);
                    observedPaymentMethods[pair.RideId] = payload.GetProperty("paymentMethodCode").GetString()!;
                }
                if (name == "stateChanged") {
                    Interlocked.Increment(ref stateReceived);
                    Add("stateCreatedToClientMs", (DateTime.UtcNow - payload.GetProperty("createdAtUtc").GetDateTime().ToUniversalTime()).TotalMilliseconds);
                }
                if (name == "driverLocationUpdated") {
                    var at = payload.GetProperty("observedAtUtc").GetDateTime().ToUniversalTime();
                    if (observed.TryGetValue(pair.RideId, out var previous) && at <= previous) Interlocked.Increment(ref outOfOrder);
                    observed[pair.RideId] = at;
                }
            }};
            await driver.ConnectAsync(ready.Url, pair.DriverToken); await customer.ConnectAsync(ready.Url, pair.CustomerToken);
            await driver.InvokeAsync("JoinRide", pair.RideId); await customer.InvokeAsync("JoinRide", pair.RideId);
            peers.Add(driver); peers.Add(customer);
        }
        var administratorCount = pairs.Length >= 500 ? 10 : pairs.Length >= 200 ? 5 : pairs.Length >= 100 ? 2 : 1;
        for (var a = 0; a < administratorCount; a++) {
            var admin = new HubProbe(); await admin.ConnectAsync(ready.Url, ready.AdminToken); await admin.InvokeAsync("JoinOperations"); peers.Add(admin);
        }
        // Warm both measured query families with bounded concurrency. First-use
        // compilation belongs to setup, not the steady-state capacity interval.
        await Parallel.ForEachAsync(Enumerable.Range(0, pairs.Length), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) => {
            var pair = pairs[i];
            if (durableSamples) {
                var warm = new RouteSampleRequest(Guid.NewGuid(), 1, 15.3694, 44.1910, 90d, 0d, 5d, DateTime.UtcNow);
                var receipt = await SendRouteSample(pair, i, warm, useHttp: i % 2 == 0, measured: false);
                ValidateReceipt(receipt, warm);
            } else {
                await peers[i * 2].InvokeAsync("PublishDriverLocation", new {
                    rideId = pair.RideId, latitude = 15.3694, longitude = 44.1910, bearing = 90d, speed = 0d, accuracy = 5d, observedAtUtc = DateTime.UtcNow
                });
            }
            await Read(pair, pair.CustomerToken); await Read(pair, pair.DriverToken);
        });
        await Task.Delay(2000);
        metrics.Clear(); observed.Clear(); gpsReceived = 0; outOfOrder = 0;
        before = await http.GetFromJsonAsync<JsonElement>("benchmark/metrics");
        using var duration = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var clock = Stopwatch.StartNew();
        Console.WriteLine($"RUNNING: {peers.Count} authenticated WebSocket clients, {seconds}s, separate load-generator process.");
        var checkpoint = Task.Run(async () => {
            try {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (await timer.WaitForNextTickAsync(duration.Token)) {
                    var progress = new { completed = false, targetSeconds = seconds, elapsedSeconds = clock.Elapsed.TotalSeconds,
                        pairs = pairs.Length, connections = peers.Count, failures, failureTypes, failureSamples, expectedGps, gpsReceived, outOfOrder, stateReceived, mutations, recovered, reconnects,
                        durableSamples, routeGenerated, routeAcknowledged, routeRetries, routeDuplicateChecks, routeHttpAttempts, routeHubAttempts, pendingRouteSamples = pendingRouteSamples.Count,
                        generatorWorkingSet = Environment.WorkingSet, generatorCpuMs = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds };
                    await File.WriteAllTextAsync(resultFile + ".partial.json", JsonSerializer.Serialize(progress, jsonOptions));
                }
            } catch (OperationCanceledException) when (duration.IsCancellationRequested) { }
        });
        async Task Read(Pair pair, string token, string? expectedPaymentMethod = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/execute") { Content = JsonContent.Create(new { model = "RideModel", operation = "get",
                data = new { id = pair.RideId, includeRouteHistory = token != pair.CustomerToken } }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var timing = Stopwatch.StartNew();
            using var response = await http.SendAsync(request);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!response.IsSuccessStatusCode || !result.GetProperty("success").GetBoolean()) throw BenchmarkApiFailure.From(response, result);
            if (expectedPaymentMethod is not null && result.GetProperty("data").GetProperty("ride").GetProperty("paymentMethodCode").GetString() != expectedPaymentMethod)
                throw new InvalidDataException("Canonical recovery did not return the last committed payment choice.");
            Add("rideSnapshotMs", timing.Elapsed.TotalMilliseconds);
        }
        async Task<JsonElement> SendRouteSample(Pair pair, int index, RouteSampleRequest sample, bool useHttp, bool measured = true) {
            var payload = new { rideId = pair.RideId, sessionId = routeSessionIds[index], samples = new[] { new {
                sampleId = sample.SampleId, sequence = sample.Sequence, latitude = sample.Latitude, longitude = sample.Longitude,
                bearing = sample.Bearing, speed = sample.Speed, accuracy = sample.Accuracy, observedAtUtc = sample.ObservedAtUtc
            } } };
            if (!useHttp) {
                if (measured) Interlocked.Increment(ref routeHubAttempts);
                return await peers[index * 2].InvokeAsync("PublishDriverLocationBatch", payload);
            }
            if (measured) Interlocked.Increment(ref routeHttpAttempts);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/tracking/driver-location-batch") { Content = JsonContent.Create(payload) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.DriverToken);
            using var response = await http.SendAsync(request);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!response.IsSuccessStatusCode || !result.GetProperty("success").GetBoolean()) throw BenchmarkApiFailure.From(response, result);
            return result.GetProperty("data").Clone();
        }
        static void ValidateReceipt(JsonElement receipt, RouteSampleRequest sample) {
            if (!receipt.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean() ||
                !receipt.TryGetProperty("acceptedSampleIds", out var ids) || ids.ValueKind != JsonValueKind.Array ||
                ids.GetArrayLength() != 1 || ids[0].GetGuid() != sample.SampleId ||
                !receipt.TryGetProperty("lastContiguousSequence", out var contiguous) || contiguous.GetInt64() != sample.Sequence)
                throw new InvalidDataException("Durable sample identity or contiguous acknowledgement mismatch.");
        }
        var tasks = pairs.Select((pair, i) => Task.Run(async () => {
            var iteration = 0; var nextRiderRead = 0d; var nextDriverRead = 0d; var nextMutation = 3d;
            var nextPaymentMethod = "wallet"; // Seeded cash; every successful command changes the choice.
            long sequence = 1; // Each durable session's first point was warmed above.
            PendingRouteSample? pending = null;
            var reconnected = false;
            await Task.Delay((i * 37) % 1300);
            while (!duration.IsCancellationRequested) {
                var operation = "gps";
                try {
                    var now = clock.Elapsed.TotalSeconds;
                    var timing = Stopwatch.StartNew();
                    if (durableSamples) {
                        if (pending is null) {
                            pending = new(new RouteSampleRequest(Guid.NewGuid(), ++sequence, 15.3694,
                                44.1910 + .008 * Math.Sin(now / 300d), 90d, 3d, 5d, DateTime.UtcNow));
                            pendingRouteSamples[pair.RideId] = pending;
                            Interlocked.Increment(ref routeGenerated);
                        }
                        // An uncertain response never allocates a replacement ID,
                        // timestamp or sequence. Retry the same point over the other transport.
                        if (!pending.Acknowledged) {
                            if (pending.Attempts++ > 0) Interlocked.Increment(ref routeRetries);
                            var receipt = await SendRouteSample(pair, i, pending.Sample, (sequence + pending.Attempts) % 2 == 0);
                            ValidateReceipt(receipt, pending.Sample);
                            pending.Acknowledged = true;
                            Interlocked.Increment(ref routeAcknowledged);
                            Interlocked.Increment(ref expectedGps);
                            Add("gpsIngestAckMs", timing.Elapsed.TotalMilliseconds);
                            acknowledged[pair.RideId] = pending.Sample.ObservedAtUtc;
                        }
                        if (sequence % 10 == 0) {
                            operation = "gpsDuplicate";
                            if (pending.DuplicateAttempts++ > 0) Interlocked.Increment(ref routeRetries);
                            var duplicate = await SendRouteSample(pair, i, pending.Sample, (sequence + pending.DuplicateAttempts) % 2 != 0);
                            ValidateReceipt(duplicate, pending.Sample);
                            Interlocked.Increment(ref routeDuplicateChecks);
                        }
                        pendingRouteSamples.TryRemove(pair.RideId, out _);
                        pending = null;
                    } else {
                        var at = DateTime.UtcNow;
                        await peers[i * 2].InvokeAsync("PublishDriverLocation", new { rideId = pair.RideId, latitude = 15.3694,
                            longitude = 44.1910 + .008 * Math.Sin(now / 300d), bearing = 90d, speed = 3d, accuracy = 5d, observedAtUtc = at });
                        Interlocked.Increment(ref expectedGps); Add("gpsIngestAckMs", timing.Elapsed.TotalMilliseconds);
                        acknowledged[pair.RideId] = at;
                    }
                    if (now >= nextRiderRead) { operation = "riderSnapshot"; await Read(pair, pair.CustomerToken); nextRiderRead = now + 10; }
                    if (now >= nextDriverRead) { operation = "driverSnapshot"; await Read(pair, pair.DriverToken); nextDriverRead = now + 15; }
                    if (mixed && now >= nextMutation) {
                        operation = "paymentChoice";
                        using var change = new HttpRequestMessage(HttpMethod.Post, "api/execute") {
                            Content = JsonContent.Create(new { model = "RideModel", operation = "update", data = new { id = pair.RideId, paymentMethodCode = nextPaymentMethod } })
                        };
                        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pair.CustomerToken);
                        var mutationTimer = Stopwatch.StartNew();
                        using var response = await http.SendAsync(change);
                        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                        if (!response.IsSuccessStatusCode || !result.GetProperty("success").GetBoolean()) throw BenchmarkApiFailure.From(response, result);
                        Interlocked.Increment(ref mutations); Add("paymentChoiceMutationMs", mutationTimer.Elapsed.TotalMilliseconds);
                        expectedPaymentMethods[pair.RideId] = nextPaymentMethod;
                        nextPaymentMethod = nextPaymentMethod == "wallet" ? "cash" : "wallet";
                        nextMutation = now + 10;
                    }
                    if (mixed && !reconnected && now >= seconds / 2d) {
                        operation = "reconnectRecovery";
                        reconnected = true;
                        var previous = peers[i * 2 + 1]; var callback = previous.OnEvent;
                        await previous.DisposeAsync();
                        var reconnectTimer = Stopwatch.StartNew();
                        var replacement = new HubProbe { OnEvent = callback };
                        peers[i * 2 + 1] = replacement;
                        await replacement.ConnectAsync(ready.Url, pair.CustomerToken);
                        await replacement.InvokeAsync("JoinRide", pair.RideId);
                        string cursor = "";
                        do {
                            var page = await replacement.InvokeAsync("ReplayStateChanges", new Dictionary<string, long>(), cursor);
                            if (!page.GetProperty("supported").GetBoolean()) {
                                if (durableEvents) throw new IOException("Journal recovery disabled.");
                                break;
                            }
                            if (!durableEvents) throw new InvalidDataException("Unexpected journal recovery in default mode.");
                            Interlocked.Add(ref recovered, page.GetProperty("events").GetArrayLength());
                            if (!page.GetProperty("hasMore").GetBoolean()) break;
                            cursor = page.GetProperty("nextStream").GetString()!;
                        } while (true);
                        await Read(pair, pair.CustomerToken, expectedPaymentMethods.GetValueOrDefault(pair.RideId, "cash"));
                        if (!durableEvents) Interlocked.Increment(ref recovered);
                        Interlocked.Increment(ref reconnects); Add("reconnectCanonicalRecoveryMs", reconnectTimer.Elapsed.TotalMilliseconds);
                    }
                } catch (Exception error) {
                    Interlocked.Increment(ref failures);
                    var code = error is BenchmarkApiFailure apiFailure ? apiFailure.Code : null;
                    var key = operation + ":" + error.GetType().Name + (code is null ? "" : ":" + code);
                    failureTypes.AddOrUpdate(key, 1, (_, count) => count + 1);
                    if (Interlocked.Increment(ref sampledFailures) <= 16)
                        failureSamples.Enqueue(new { operation, errorType = error.GetType().Name, code,
                            httpStatus = error is BenchmarkApiFailure failure ? (int?)failure.Status : null });
                }
                iteration++;
                try { await Task.Delay(1200 + (iteration + i) % 7 * 200, duration.Token); } catch (OperationCanceledException) { break; }
            }
        })).ToArray();
        await Task.WhenAll(tasks); await checkpoint;
        // Buffered visuals may coalesce intermediate locations. Require the last
        // acknowledged point for every driver to arrive, with a bounded drain.
        var drain = Stopwatch.StartNew();
        while (drain.Elapsed < TimeSpan.FromSeconds(10) && (acknowledged.Any(x => !observed.TryGetValue(x.Key, out var at) || at < x.Value) ||
            expectedPaymentMethods.Any(x => !observedPaymentMethods.TryGetValue(x.Key, out var method) || method != x.Value)))
            await Task.Delay(100);
        var finalLocationMismatches = acknowledged.Count(x => !observed.TryGetValue(x.Key, out var at) || at < x.Value);
        var finalPaymentMismatches = expectedPaymentMethods.Count(x => !observedPaymentMethods.TryGetValue(x.Key, out var method) || method != x.Value);
        var failedConnections = peers.Count(x => x.Failure is not null);
        var after = await http.GetFromJsonAsync<JsonElement>("benchmark/metrics");
        var bufferedVisual = after.TryGetProperty("flags", out var flags) && flags.GetProperty("bufferedVisualDelivery").GetBoolean();
        var routeSampleDelta = durableSamples ? after.GetProperty("routeSamples").GetInt64() - before.GetProperty("routeSamples").GetInt64() : 0;
        var routeArchiveDelta = after.GetProperty("routeArchive").GetInt64() - before.GetProperty("routeArchive").GetInt64();
        var routeSessionsWithGaps = durableSamples ? after.GetProperty("routeSessionsWithGaps").GetInt64() : 0;
        var routeHistoryMismatch = durableSamples ? (routeGenerated != routeAcknowledged || pendingRouteSamples.Count != 0 ||
            routeSampleDelta != routeAcknowledged || routeArchiveDelta != routeAcknowledged || routeSessionsWithGaps != 0) : routeArchiveDelta != expectedGps;
        var samples = metrics.ToDictionary(x => x.Key, x => {
            var values = x.Value.Order().ToArray();
            double P(double p) => values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * p) - 1)];
            return new { count = values.Length, p50 = P(.5), p95 = P(.95), p99 = P(.99), max = values.LastOrDefault() };
        });
        Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(new { kind = mixed ? "desktop-gps-payment-choice-and-reconnect" : "desktop-websocket-gps-and-ride-read-baseline", deviceMeasurement = false,
            completed = true, version = typeof(TrackingHub).Assembly.GetName().Version?.ToString(), pairs = pairs.Length, administratorCount, connections = peers.Count, durationSeconds = clock.Elapsed.TotalSeconds,
            setupSeconds = setup.Elapsed.TotalSeconds - clock.Elapsed.TotalSeconds, failures, failureTypes, failureSamples, expectedGps, gpsReceived, outOfOrder, stateReceived, mutations, recovered, reconnects,
            durableEvents, legacyStateReceived, recoveryMode = durableEvents ? "journal-and-canonical-rest" : "canonical-rest", riderIncludeRouteHistory = false,
            durableSamples, routeGenerated, routeAcknowledged, routeRetries, routeDuplicateChecks, routeHttpAttempts, routeHubAttempts,
            pendingRouteSamples = pendingRouteSamples.Count, routeSampleDelta, routeArchiveDelta, routeSessionsWithGaps, routeHistoryMismatch,
            finalLocationMismatches, finalPaymentMismatches, failedConnections, visualDrainSeconds = drain.Elapsed.TotalSeconds,
            generatorWorkingSet = Environment.WorkingSet, generatorCpuMs = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds,
            sqlCommands = after.GetProperty("commands").GetInt64() - before.GetProperty("commands").GetInt64(), serverCpuMs = after.GetProperty("cpuMs").GetDouble() - before.GetProperty("cpuMs").GetDouble(), serverBefore = before, serverAfter = after, samples,
            limitations = new[] { "No physical phones or frame rendering measured", "No live payment providers or FCM", mixed ? "Changes payment choice only, not money movement or offer acceptance; reconnects clustered around halfway" : "No reconnect or business mutation burst in baseline", "Loopback network; same machine, separate server/generator processes", "Extended test access-token lifetime; refresh-token traffic excluded", "State latency starts at journal creation before commit, not actual device application",
                durableSamples ? "One-point durable batches over alternating HTTP/Hub transports, duplicate every tenth sequence; no injected response loss" : "Legacy GPS transport; durable route receipts not measured" } }, jsonOptions));
        Console.WriteLine($"RESULT: failures={failures}, GPS received={gpsReceived}, ordering violations={outOfOrder}; metrics saved to {resultFile}");
        if (failures > 0 || outOfOrder > 0 || failedConnections > 0 || finalLocationMismatches > 0 || finalPaymentMismatches > 0 ||
            routeHistoryMismatch || !mixed && !bufferedVisual && !durableSamples && expectedGps != gpsReceived ||
            mixed && (mutations == 0 || (durableEvents ? stateReceived == 0 : legacyStateReceived == 0) || reconnects != pairs.Length)) Environment.ExitCode = 1;
    }
    finally { foreach (var peer in peers) await peer.DisposeAsync(); }
}

static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
static AuthSession Session(User user, string token) => new() { User = user, FamilyId = Guid.NewGuid(), DeviceId = "benchmark-only",
    AccessTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
    RefreshTokenHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), AccessExpiresAtUtc = DateTime.UtcNow.AddHours(6), SessionExpiresAtUtc = DateTime.UtcNow.AddHours(6) };
record Pair(int RideId, int DriverId, string DriverToken, string CustomerToken);
record Ready(string Url, string AdminToken, Pair[] Pairs, string Database);
sealed class PendingRouteSample(RouteSampleRequest sample)
{
    public RouteSampleRequest Sample { get; } = sample;
    public int Attempts, DuplicateAttempts;
    public bool Acknowledged;
}
sealed class BenchmarkApiFailure(int status, string code) : IOException("Benchmark API rejected the request.")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static BenchmarkApiFailure From(HttpResponseMessage response, JsonElement result)
    {
        var raw = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString() : null;
        // Diagnostics record only bounded machine codes/statuses, never bodies,
        // authorization headers, session tokens or SQL command text.
        var safe = raw is { Length: > 0 and <= 80 } && raw.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? raw : "unclassified_response";
        return new((int)response.StatusCode, safe);
    }
}
sealed class CommandCounter : DbCommandInterceptor
{
    public long Count;
    public ConcurrentDictionary<string, long> Failures { get; } = new();
    public ConcurrentDictionary<string, long> Families { get; } = new();
    public ConcurrentDictionary<string, long> ElapsedTicks { get; } = new();
    private static string Family(DbCommand command)
    {
        var sql = command.CommandText;
        // Fixed labels only. Never copy query text or parameter values into
        // benchmark artifacts. Elapsed command time includes SQL waits, not CPU.
        if (sql.Contains("sp_getapplock", StringComparison.Ordinal)) return "route-lock";
        if (sql.Contains("INSERT INTO", StringComparison.Ordinal) || sql.Contains("UPDATE [", StringComparison.Ordinal) ||
            sql.Contains("DELETE FROM", StringComparison.Ordinal)) return "writes";
        foreach (var table in new[] { "DriverRatings", "AuthSessions", "DriverProfiles", "DriverLiveLocations",
            "LocationUpdates", "RideSafetyShares", "RealtimeStateEvents", "Rides" })
            if (sql.Contains("[" + table + "]", StringComparison.Ordinal)) return "read-" + table;
        return "other";
    }
    private void Completed(DbCommand command, CommandExecutedEventData data)
    {
        var family = Family(command);
        Families.AddOrUpdate(family, 1, (_, count) => count + 1);
        ElapsedTicks.AddOrUpdate(family, data.Duration.Ticks, (_, ticks) => ticks + data.Duration.Ticks);
    }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default) { Completed(command, eventData); return ValueTask.FromResult(result); }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default) { Completed(command, eventData); return ValueTask.FromResult(result); }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        object? result, CancellationToken cancellationToken = default) { Completed(command, eventData); return ValueTask.FromResult(result); }
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        var key = eventData.Exception is Microsoft.Data.SqlClient.SqlException sql
            ? $"SqlException:{sql.Number}" : eventData.Exception.GetType().Name;
        Failures.AddOrUpdate(key, 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
}
