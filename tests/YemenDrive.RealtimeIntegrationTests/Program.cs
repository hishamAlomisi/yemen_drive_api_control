using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api;
using YemenDrive.Api.Realtime;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Database.Realtime;

if (args is ["--termination-test", var phase, var childDatabase, var childWalletText]) {
    if (!Regex.IsMatch(childDatabase, "^YemenDrive_RealtimeTests_[a-f0-9]{32}$") ||
        phase is not ("before-commit" or "after-commit") || !int.TryParse(childWalletText, out var childWallet))
        throw new InvalidOperationException("Invalid isolated child test.");
    var childOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer($"Server=(localdb)\\MSSQLLocalDB;Database={childDatabase};Integrated Security=True;TrustServerCertificate=True").Options;
    await using var child = new YemenDriveDbContext(childOptions, new RealtimeJournalSettings { Enabled = true });
    await using var childTransaction = await child.Database.BeginTransactionAsync();
    (await child.Wallets.SingleAsync(x => x.Id == childWallet)).Balance += 1;
    await child.SaveChangesAsync();
    if (phase == "after-commit") await childTransaction.CommitAsync();
    // Exit skips finally/disposal, reproducing process loss without an OS crash dialog.
    Environment.Exit(29);
    return;
}

var name = "YemenDrive_RealtimeTests_" + Guid.NewGuid().ToString("N");
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={name};Integrated Security=True;TrustServerCertificate=True";
var baseOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
var settings = new RealtimeJournalSettings { Enabled = true };
var configFile = Path.Combine(Path.GetTempPath(), name + ".json");
var config = new DatabaseConfigurationStore(configFile);
await config.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = name, IntegratedSecurity = true }, default);
YemenDriveDbContext Open(bool enabled = true) => new(baseOptions, enabled ? settings : null);
try {
    await using var seed = Open(false);
    await seed.GetService<IMigrator>().MigrateAsync("20261103175608_DurableDriverRouteSamples");
    var legacyEventId = Guid.NewGuid();
    await seed.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [RealtimeStateEvents] ([Id],[StreamKey],[Version],[Resource],[ResourceId],[Changes],[CreatedAtUtc],[Attempts]) VALUES ({legacyEventId},N'migration:0',1,N'migration',0,N'*',SYSUTCDATETIME(),0)");
    await seed.Database.MigrateAsync();
    Check(!seed.Database.HasPendingModelChanges(), "additive migration matches the runtime model");
    Check(await seed.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'dbo.RideOffers') AND [name] = N'IX_RideOffers_RideId_DriverId' AND [has_filter] = 0").SingleAsync() == 1,
        "additive migration provisions an unfiltered offer audience index for every offer status");
    Check(await seed.RealtimeStateEvents.Where(x => x.Id == legacyEventId).Select(x => x.OperationId).SingleAsync() == Guid.Empty,
        "correlation migration preserves prior journal rows with an explicit unknown operation identity");
    await seed.RealtimeStateEvents.Where(x => x.Id == legacyEventId).ExecuteDeleteAsync();
    var customer = new User { PhoneNumber = "journal-customer", Role = UserRole.Customer };
    var driver = new User { PhoneNumber = "journal-driver", Role = UserRole.Driver };
    var outsider = new User { PhoneNumber = "journal-outsider", Role = UserRole.Customer };
    var admin = new User { PhoneNumber = "journal-admin", Role = UserRole.Admin };
    var kind = new ServiceKind { Code = "journal-test", NameAr = "Test" };
    var catalog = new RideServiceCatalogItem { Code = "journal-car", NameAr = "Test", ServiceKind = kind };
    var ride = new Ride { Customer = customer, Driver = driver, ServiceKind = kind, ServiceCatalogItem = catalog, Status = RideStatus.DriverAssigned };
    var wallet = new Wallet { User = customer, Balance = 500 };
    seed.Rides.Add(ride); seed.Wallets.Add(wallet); seed.Users.AddRange(outsider, admin);
    await seed.SaveChangesAsync();
    Check(await seed.RealtimeStateEvents.CountAsync() == 0, "rollout disabled leaves the original write path without journal events");

    await using (var db = Open()) {
        await using var tx = await db.Database.BeginTransactionAsync();
        var item = await db.Rides.SingleAsync(x => x.Id == ride.Id);
        item.CustomerPaymentEnabled = true;
        await db.SaveChangesAsync();
        Check(await db.RealtimeStateEvents.CountAsync() == 1, "state change and journal exist inside the same transaction");
        Check((await db.RealtimeStateEvents.SingleAsync()).OperationId == tx.TransactionId,
            "explicit transaction identity is recorded on the persisted event");
        item.CustomerPaymentEnabled = false;
        await db.SaveChangesAsync();
        var transactionEvents = await db.RealtimeStateEvents.AsNoTracking().ToListAsync();
        Check(transactionEvents.Count == 2 && transactionEvents.All(x => x.OperationId == tx.TransactionId),
            "successive saves in one transaction share one correlation identity");
        await tx.RollbackAsync();
    }
    await using (var verify = Open()) {
        Check(await verify.RealtimeStateEvents.CountAsync() == 0 && !await verify.Rides.Where(x => x.Id == ride.Id).Select(x => x.CustomerPaymentEnabled).SingleAsync(), "rollback removes both state and event");
    }

    var commitSignal = new RealtimeCommitSignal();
    var interceptor = new RealtimeCommitInterceptor(commitSignal);
    var interceptOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection)
        .AddInterceptors(new WalletMovementNotificationInterceptor(), new NotificationSaveCaptureInterceptor(), interceptor).Options;
    Guid implicitOperationId;
    await using (var db = new YemenDriveDbContext(interceptOptions, settings)) {
        var item = await db.Wallets.SingleAsync(x => x.Id == wallet.Id);
        item.Balance += 100;
        db.WalletTransactions.Add(new WalletTransaction { WalletId = item.Id, Type = WalletTransactionType.Credit, Amount = 100, BalanceAfter = 600 });
        await db.SaveChangesAsync();
        Check(await db.RealtimeStateEvents.AnyAsync(x => x.StreamKey == $"wallet:{wallet.Id}"), "implicit wallet save journals the committed wallet change");
        Check(await db.RealtimeStateEvents.AnyAsync(x => x.StreamKey == $"notifications:{customer.Id}"), "notifications created by save interceptors are also journalled");
        var operationIds = await db.RealtimeStateEvents.Select(x => x.OperationId).Distinct().ToListAsync();
        Check(operationIds.Count == 1 && operationIds[0] != Guid.Empty,
            "wallet and interceptor-created notification streams share the implicit transaction identity");
        implicitOperationId = operationIds[0];
        Check(!db.ConsumeRealtimeCommitSignal(), "commit interceptor consumes the wakeup only after actual commit");
    }

    var failingOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).AddInterceptors(new FailJournal()).Options;
    await using (var db = new YemenDriveDbContext(failingOptions, settings)) {
        var item = await db.Wallets.SingleAsync(x => x.Id == wallet.Id); item.Balance = 999;
        var failed = false;
        try { await db.SaveChangesAsync(); } catch (IOException) { failed = true; }
        Check(failed, "fault injection reaches journal persistence after business DML");
    }
    await using (var db = Open()) Check(await db.Wallets.Where(x => x.Id == wallet.Id).Select(x => x.Balance).SingleAsync() == 600, "journal failure cannot leave a committed wallet mutation");

    // Independent child inserts share one logical stream. Allocation must serialize
    // on the database, including simultaneous creation of a previously absent head.
    var saves = Enumerable.Range(0, 16).Select(async i => {
        await using var db = Open();
        db.CommunicationMessages.Add(new CommunicationMessage { RideId = ride.Id, SenderId = customer.Id, RecipientId = driver.Id, Content = $"message-{i}" });
        await db.SaveChangesAsync();
    });
    await Task.WhenAll(saves);
    await using (var db = Open()) {
        var versions = await db.RealtimeStateEvents.Where(x => x.StreamKey == $"messages:{ride.Id}").OrderBy(x => x.Version).Select(x => x.Version).ToListAsync();
        Check(versions.SequenceEqual(Enumerable.Range(1, 16).Select(x => (long)x)), "concurrent commits allocate unique contiguous per-resource versions");
        var messageOperations = await db.RealtimeStateEvents.Where(x => x.StreamKey == $"messages:{ride.Id}").Select(x => x.OperationId).Distinct().ToListAsync();
        Check(messageOperations.Count == 16 && messageOperations.All(x => x != Guid.Empty && x != implicitOperationId),
            "independent concurrent transactions do not reuse correlation identities");
        var recovery = new RealtimeStateRecovery(db, settings);
        var page = await recovery.ReadAsync(customer.Id, null, default);
        Check(page.Supported && page.Events.Count == 3 && !page.HasMore && page.Events.Single(x => x.Resource == "messages").Version == 16 && page.Events.All(x => x.Changes == "*"), "canonical recovery covers all committed changes with the latest version per resource");
        Check(page.Events.Where(x => x.Resource is "wallet" or "notifications").All(x => x.OperationId == implicitOperationId) &&
            page.Events.All(x => x.OperationId != Guid.Empty), "canonical replay preserves persisted transaction correlation");
        var known = page.Events.GroupBy(x => x.Stream).ToDictionary(x => x.Key, x => x.Max(e => e.Version));
        Check((await recovery.ReadAsync(customer.Id, known, default)).Events.Count == 0, "per-stream acknowledgement filters duplicates without a global identity cursor");
        Check((await recovery.ReadAsync(outsider.Id, null, default)).Events.Count == 0, "unrelated authenticated user cannot replay someone else's state");
        Check((await recovery.ReadAsync(admin.Id, null, default)).Events.All(x => x.Resource != "messages" && x.Resource != "notifications"), "operations audience does not include private chat or another user's notifications");
        Check((await new RealtimeStateRecovery(db, new()).ReadAsync(customer.Id, null, default)).Supported == false, "old-mode compatibility reports recovery as disabled");
        var blocked = false;
        try { await recovery.ReadAsync(customer.Id, new() { [new string('x', 121)] = 0 }, default); } catch (HubException) { blocked = true; }
        Check(blocked, "replay bounds are validated rather than accepting unbounded client state");
    }

    // Demonstrate why a global sequence is not a safe recovery cursor: an event
    // created earlier can commit after a different stream has already been applied.
    var nonblockingDelivered = 0;
    await using (var delayed = Open()) {
        await using var tx = await delayed.Database.BeginTransactionAsync();
        var item = await delayed.Rides.SingleAsync(x => x.Id == ride.Id); item.CustomerPaymentEnabled = true;
        await delayed.SaveChangesAsync();
        await using (var other = Open()) {
            other.SavedPlaces.Add(new SavedPlace { UserId = customer.Id, Label = "home", Address = "Test", Latitude = 15, Longitude = 44, LocationKey = "journal-test" });
            await other.SaveChangesAsync();
        }
        var independentServices = new ServiceCollection();
        independentServices.AddDbContext<YemenDriveDbContext>(o => o.UseSqlServer(connection));
        await using var independentProvider = independentServices.BuildServiceProvider();
        var independentTransport = new TestHubContext(); independentTransport.Proxy.Fail = false;
        var independentWorker = new RealtimeStateDeliveryWorker(independentProvider.GetRequiredService<IServiceScopeFactory>(), commitSignal, settings, config,
            independentTransport, NullLogger<RealtimeStateDeliveryWorker>.Instance);
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        nonblockingDelivered = await independentWorker.DeliverAsync(bounded.Token);
        Check(nonblockingDelivered > 0, "an uncommitted stream does not block delivery of other committed resources");
        await tx.CommitAsync();
    }
    await using (var db = Open()) {
        var recovery = new RealtimeStateRecovery(db, settings);
        var page = await recovery.ReadAsync(customer.Id, new() { [$"places:{customer.Id}"] = 1 }, default);
        Check(page.Events.Any(x => x.Resource == "ride") && page.Events.All(x => x.Resource != "places"), "late commit in another stream is never skipped by an unrelated newer acknowledgement");
    }

    // Worker retry with a failing transport; publication does not delete recovery.
    var services = new ServiceCollection();
    services.AddDbContext<YemenDriveDbContext>(o => o.UseSqlServer(connection));
    await using var provider = services.BuildServiceProvider();
    var transport = new TestHubContext();
    var worker = new RealtimeStateDeliveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), commitSignal, settings, config, transport, NullLogger<RealtimeStateDeliveryWorker>.Instance);
    var attempts = await worker.DeliverAsync(default);
    Check(attempts == 0 && transport.Proxy.Attempts > 0, "failed transport leaves events pending rather than claiming successful delivery");
    transport.Proxy.Fail = false;
    await using (var db = Open(false)) await db.RealtimeStateEvents.Where(x => x.PublishedAtUtc == null).ExecuteUpdateAsync(x => x.SetProperty(e => e.LeaseUntilUtc, (DateTime?)null));
    var total = 0;
    while ((attempts = await worker.DeliverAsync(default)) > 0) total += attempts;
    await using (var db = Open()) {
        Check(total + nonblockingDelivered == await db.RealtimeStateEvents.CountAsync() && !await db.RealtimeStateEvents.AnyAsync(x => x.PublishedAtUtc == null), "worker drains committed events after the transport recovers");
        Check((await new RealtimeStateRecovery(db, settings).ReadAsync(customer.Id, null, default)).Events.Count > 0, "published events remain available after client disconnection");
    }
    await using (var db = Open(false)) await db.RealtimeStateEvents.Where(x => x.Resource == "messages" && x.Version < 16)
        .ExecuteUpdateAsync(x => x.SetProperty(e => e.PublishedAtUtc, DateTime.UtcNow.AddDays(-8)));
    Check(await worker.PrunePublishedHistoryAsync(default) == 15, "retention deletes only superseded published history");
    await using (var db = Open()) {
        var replay = await new RealtimeStateRecovery(db, settings).ReadAsync(customer.Id, null, default);
        Check(replay.Events.Single(x => x.Resource == "messages").Version == 16, "expired history still recovers the latest canonical resource version");
    }
    await TerminateChild("before-commit", name, wallet.Id);
    await using (var db = Open()) Check(await db.Wallets.Where(x => x.Id == wallet.Id).Select(x => x.Balance).SingleAsync() == 600 &&
        await db.RealtimeStateEvents.CountAsync(x => x.StreamKey == $"wallet:{wallet.Id}") == 1,
        "actual process exit before commit preserves neither mutation nor event");
    await TerminateChild("after-commit", name, wallet.Id);
    await using (var db = Open()) Check(await db.Wallets.Where(x => x.Id == wallet.Id).Select(x => x.Balance).SingleAsync() == 601 &&
        (await new RealtimeStateRecovery(db, settings).ReadAsync(customer.Id, null, default)).Events.Any(x => x.Stream == $"wallet:{wallet.Id}" && x.Version == 2),
        "actual process exit after commit and before broadcast leaves the mutation recoverable");
    await using (var db = Open(false)) {
        var eventsBefore = await db.RealtimeStateEvents.CountAsync();
        var retainedBalance = await db.Wallets.Where(x => x.Id == wallet.Id).Select(x => x.Balance).SingleAsync();
        (await db.Wallets.SingleAsync(x => x.Id == wallet.Id)).Balance += 1;
        await db.SaveChangesAsync();
        Check(await db.RealtimeStateEvents.CountAsync() == eventsBefore && retainedBalance == 601,
            "rollback through feature-off write mode preserves already committed history and money");
    }
    await using (var db = Open()) {
        db.PaymentTransactions.Add(new PaymentTransaction { UserId = driver.Id, Amount = 15, Provider = "isolated-test", Status = PaymentStatus.Pending });
        await db.SaveChangesAsync();
        Check(await db.RealtimeStateEvents.AnyAsync(x => x.StreamKey == $"account:{driver.Id}" && x.Changes.Contains("PaymentTransaction")), "standalone payment transaction state has an owner event even without a ride or wallet mutation");
        db.PaymentWalletOperations.Add(new PaymentWalletOperation { CustomerId = customer.Id, PaymentMethod = new PaymentMethod { Code = "replay-wallet", NameAr = "Replay test" }, RequestId = "replay-request", Amount = 20 });
        db.JournalEntries.Add(new JournalEntry { EntryNumber = "replay-ledger", Reference = "replay-ledger", Description = "Isolated test draft" });
        await db.SaveChangesAsync();
        Check(await db.RealtimeStateEvents.AnyAsync(x => x.StreamKey == $"account:{customer.Id}"), "standalone payment state without a ride invalidates its owner account");
        Check(await db.RealtimeStateEvents.AnyAsync(x => x.Resource == "ledger" && x.Recipients.All(r => r.UserId == 0)), "administrative journal changes invalidate authorized operations without private ledger payloads");
    }
    await using (var db = Open(false)) {
        db.RealtimeStateEvents.AddRange(Enumerable.Range(0, 650).Select(i => new RealtimeStateEvent {
            Id = Guid.NewGuid(), StreamKey = $"page:{i:D4}", Version = 1, Resource = "ride", ResourceId = i + 1,
            CreatedAtUtc = DateTime.UtcNow, Recipients = [new RealtimeStateRecipient { UserId = outsider.Id }]
        }));
        await db.SaveChangesAsync();
        var recovery = new RealtimeStateRecovery(db, settings);
        var seen = new HashSet<string>(); string? cursor = null; var pages = 0;
        do {
            var page = await recovery.ReadAsync(outsider.Id, null, default, cursor);
            foreach (var change in page.Events) if (!seen.Add(change.Stream)) throw new InvalidOperationException("Repeated keyset stream: " + change.Stream);
            pages++;
            if (!page.HasMore) break;
            Check(page.NextStream != null && page.NextStream != cursor, "replay keyset cursor progresses");
            cursor = page.NextStream;
        } while (pages < 10);
        Check(seen.Count == 651 && pages == 6, "recovery traverses over 512 streams with bounded pages including public catalog");
    }
    // Payment choice takes a SERIALIZABLE transaction and a per-ride update
    // lock. Pause all writers after DML so audience reads face the real lock
    // shape which previously let OPENJSON scan unrelated, exclusively held rides.
    var concurrentRides = Enumerable.Range(0, 25).Select(_ => new Ride {
        CustomerId = customer.Id, DriverId = driver.Id, ServiceKindId = kind.Id,
        ServiceCatalogItemId = catalog.Id, Status = RideStatus.DriverAssigned,
        PaymentMethodCode = "cash"
    }).ToArray();
    await using (var db = Open(false)) {
        db.Rides.AddRange(concurrentRides);
        await db.SaveChangesAsync();
    }
    var rideBarrier = new DistinctRideMutationBarrier(concurrentRides.Length);
    var concurrentOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection)
        .AddInterceptors(rideBarrier).Options;
    await Task.WhenAll(concurrentRides.Select(async candidate => {
        await using var db = new YemenDriveDbContext(concurrentOptions, settings);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var current = await db.Rides.FromSqlInterpolated($"SELECT * FROM [Rides] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}").SingleAsync();
        current.PaymentMethodCode = "wallet";
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }));
    await using (var db = Open(false)) {
        var ids = concurrentRides.Select(x => x.Id).ToArray();
        var streams = ids.Select(id => $"ride:{id}").ToArray();
        var committedRides = await db.Rides.CountAsync(x => ids.Contains(x.Id) && x.PaymentMethodCode == "wallet");
        // The preceding pagination fixture intentionally uses synthetic page:*
        // streams with overlapping ResourceId values. Count the real ride streams.
        var committedEvents = await db.RealtimeStateEvents.CountAsync(x => streams.Contains(x.StreamKey));
        Console.WriteLine($"CHECK: independent ride writers expected={ids.Length}, committedMutations={committedRides}, committedEvents={committedEvents}");
        Check(committedRides == ids.Length && committedEvents == ids.Length,
            "25 independent serializable ride mutations commit their state and first journal events without cross-ride audience deadlocks");
    }
    var previouslyNotifiedRideId = concurrentRides[0].Id;
    await using (var db = Open()) {
        db.Notifications.Add(new Notification { UserId = outsider.Id, Type = NotificationType.RideStatus,
            Title = "Test invitation", Body = "Isolated journal audience test", DataJson = $"{{\"rideId\":{previouslyNotifiedRideId}}}" });
        await db.SaveChangesAsync();
    }
    await using (var db = Open()) {
        (await db.Rides.SingleAsync(x => x.Id == previouslyNotifiedRideId)).CustomerPaymentEnabled = true;
        await db.SaveChangesAsync();
        var latest = await db.RealtimeStateEvents.Where(x => x.StreamKey == $"ride:{previouslyNotifiedRideId}")
            .OrderByDescending(x => x.Version).Include(x => x.Recipients).FirstAsync();
        Check(latest.Recipients.Any(x => x.UserId == outsider.Id),
            "a later ride event preserves previously notified recipients under its stream lock");
    }
    Console.WriteLine("PASS: transactional state journal, rollback, recovery permissions, concurrent versions, retry and rollout compatibility on isolated SQL.");
}
finally {
    await using var cleanup = new YemenDriveDbContext(baseOptions);
    if (!name.StartsWith("YemenDrive_RealtimeTests_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup.");
    await cleanup.Database.EnsureDeletedAsync();
    if (File.Exists(configFile)) File.Delete(configFile);
}

static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); Console.WriteLine("PASS: " + description); }
static async Task TerminateChild(string phase, string database, int walletId) {
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var value in new[] { "--termination-test", phase, database, walletId.ToString() }) start.ArgumentList.Add(value);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Failed to start isolated termination test.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await child.WaitForExitAsync(timeout.Token); }
    catch { if (!child.HasExited) child.Kill(); throw; }
    Check(child.ExitCode == 29, "isolated child process exited at " + phase);
}
sealed class FailJournal : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO [RealtimeStateEvents]", StringComparison.Ordinal)) throw new IOException("Injected journal failure.");
        return ValueTask.FromResult(result);
    }
}
sealed class DistinctRideMutationBarrier(int writers) : DbCommandInterceptor
{
    private int arrived;
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("UPDATE [Rides]", StringComparison.Ordinal) &&
            command.CommandText.Contains("[PaymentMethodCode]", StringComparison.Ordinal)) {
            if (Interlocked.Increment(ref arrived) == writers) release.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        return result;
    }
}
sealed class TestProxy : IClientProxy
{
    public bool Fail = true; public int Attempts;
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref Attempts); if (Fail) throw new IOException("Injected transport failure"); return Task.CompletedTask; }
}
sealed class TestHubContext : IHubContext<TrackingHub>
{
    public TestProxy Proxy { get; } = new();
    public IHubClients Clients => new TestClients(Proxy);
    public IGroupManager Groups => throw new NotSupportedException();
}
sealed class TestClients(TestProxy proxy) : IHubClients
{
    public IClientProxy All => proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
    public IClientProxy Client(string connectionId) => proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
    public IClientProxy Group(string groupName) => proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
    public IClientProxy User(string userId) => proxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
}
