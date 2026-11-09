using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api;
using YemenDrive.Api.Realtime;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;

var databaseName = "YemenDrive_PushDrainTests_" + Guid.NewGuid().ToString("N");
var connection = $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
var baseOptions = new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options;
var configPath = Path.Combine(Path.GetTempPath(), databaseName + ".json");
var config = new DatabaseConfigurationStore(configPath);
await config.SaveAsync(new DatabaseSettings { Provider = "SqlServer", Server = "(localdb)\\MSSQLLocalDB", Database = databaseName, IntegratedSecurity = true }, default);
var signal = new NotificationPushSignal();
var commit = new RealtimeCommitInterceptor(new RealtimeCommitSignal(), signal);
YemenDriveDbContext Open() => new(new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection)
    .AddInterceptors(new NotificationSaveCaptureInterceptor(), commit).Options);
try {
    await using var seed = new YemenDriveDbContext(baseOptions);
    await seed.Database.MigrateAsync();
    var user = new User { PhoneNumber = "push-drain-user", Role = UserRole.Driver };
    var revoked = new User { PhoneNumber = "push-revoked-user", Role = UserRole.Driver };
    var installation = new DevicePushInstallation { User = user, DeviceId = "mock-active", TokenHash = "mock-active-hash", FcmToken = "never-sent-to-firebase", Platform = "android" };
    var revokedInstallation = new DevicePushInstallation { User = revoked, DeviceId = "mock-revoked", TokenHash = "mock-revoked-hash", FcmToken = "never-sent-to-firebase-revoked", Platform = "android" };
    seed.DevicePushInstallations.AddRange(installation, revokedInstallation);
    seed.AuthSessions.Add(new AuthSession { User = user, FamilyId = Guid.NewGuid(), DeviceId = installation.DeviceId, AccessTokenHash = "mock-access", RefreshTokenHash = "mock-refresh", AccessExpiresAtUtc = DateTime.UtcNow.AddHours(1), SessionExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
    await seed.SaveChangesAsync();
    await using (var db = Open()) {
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Notifications.Add(Note(user.Id, "rollback"));
        await db.SaveChangesAsync();
        Check(signal.WakeCount == 0, "saving inside an uncommitted transaction does not wake push");
        await transaction.RollbackAsync();
        Check(signal.WakeCount == 0 && !db.ConsumeNotificationPushSignal(), "rollback discards the pending wakeup");
    }
    await using (var db = Open()) {
        Check(!await db.NotificationPushOutbox.AnyAsync(), "rollback leaves no notification outbox row");
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Notifications.Add(Note(user.Id, "commit"));
        await db.SaveChangesAsync();
        Check(signal.WakeCount == 0, "outbox durability precedes publication wakeup");
        await transaction.CommitAsync();
        Check(signal.WakeCount == 1, "actual explicit commit wakes push exactly once");
    }
    await using (var db = Open()) {
        db.Notifications.AddRange(Enumerable.Range(0, 11).Select(i => Note(user.Id, "parallel-" + i)));
        await db.SaveChangesAsync();
        Check(signal.WakeCount == 2, "implicit SQL commit also wakes push once for its batch");
    }
    var services = new ServiceCollection();
    services.AddDbContext<YemenDriveDbContext>(options => options.UseSqlServer(connection));
    await using var provider = services.BuildServiceProvider();
    var sender = new FirebaseAdminPushSender(new ConfigurationBuilder().Build());
    var mock = new MockPush { BlockFirstWave = true };
    var worker = new NotificationPushWorker(provider.GetRequiredService<IServiceScopeFactory>(), sender, config,
        NullLogger<NotificationPushWorker>.Instance, new() { DrainBatches = true }, signal, mock);
    var draining = worker.DrainOnceAsync(default);
    await mock.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Check(mock.MaximumConcurrent == 4, "four independent delivery scopes progress while the first wave is blocked");
    mock.Release.TrySetResult();
    await draining;
    Check(mock.Calls.Count == 12 && mock.Calls.Values.All(x => x == 1) && mock.MaximumConcurrent == 4,
        "one drain batch sends all twelve deliveries exactly once with concurrency bounded at four");
    await using (var db = Open()) Check(await db.NotificationPushDeliveries.CountAsync(x => x.SentAtUtc != null) == 12,
        "successful transport records durable sent status");

    await using (var db = Open()) {
        db.Notifications.AddRange(Enumerable.Range(0, 8).Select(i => Note(user.Id, "race-" + i)));
        await db.SaveChangesAsync();
    }
    await Task.WhenAll(worker.DrainOnceAsync(default), worker.DrainOnceAsync(default));
    Check(mock.Calls.Count == 20 && mock.Calls.Values.All(x => x == 1), "competing drains do not double-send claimed delivery rows");

    int expiredId, retryId;
    await using (var db = Open()) {
        var expired = Note(user.Id, "expired request");
        expired.Type = NotificationType.RideOffer;
        expired.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        expired.DataJson = "{\"action\":\"open_ride_offer\",\"rideId\":12345}";
        var retry = Note(user.Id, "retry failure");
        db.Notifications.AddRange(expired, retry);
        await db.SaveChangesAsync(); expiredId = expired.Id; retryId = retry.Id;
    }
    mock.FailNotification = retryId;
    await worker.DrainOnceAsync(default);
    Check(!mock.Calls.ContainsKey(expiredId), "expired ride invitation is never sent through the transport");
    await using (var db = Open()) {
        Check(await db.NotificationPushDeliveries.AnyAsync(x => x.Outbox.NotificationId == expiredId && x.LastErrorCode == "ride_request_no_longer_available" && x.AbandonedAtUtc != null), "expired invitation is explicitly abandoned");
        var retry = await db.NotificationPushDeliveries.SingleAsync(x => x.Outbox.NotificationId == retryId);
        Check(retry.AttemptCount == 1 && retry.NextAttemptAtUtc > DateTime.UtcNow && retry.SentAtUtc == null && retry.AbandonedAtUtc == null,
            "failed transport retains bounded exponential retry rather than marking sent");
        retry.AttemptCount = 11; retry.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }
    await worker.DrainOnceAsync(default);
    await using (var db = Open()) Check(await db.NotificationPushDeliveries.AnyAsync(x => x.Outbox.NotificationId == retryId && x.AttemptCount == 12 && x.AbandonedAtUtc != null), "twelfth failed attempt retains the existing abandonment limit");

    int revokedNotification;
    await using (var db = new YemenDriveDbContext(baseOptions)) {
        var note = Note(revoked.Id, "session revoked after recipient capture");
        db.NotificationPushOutbox.Add(new NotificationPushOutbox { Notification = note, RecipientsCapturedAtUtc = DateTime.UtcNow,
            Deliveries = [new NotificationPushDelivery { DevicePushInstallationId = revokedInstallation.Id, NextAttemptAtUtc = DateTime.UtcNow }] });
        await db.SaveChangesAsync(); revokedNotification = note.Id;
    }
    await worker.DrainOnceAsync(default);
    Check(!mock.Calls.ContainsKey(revokedNotification), "revoked device session is rechecked immediately before sending");

    await using (var db = Open()) {
        db.Notifications.AddRange(Enumerable.Range(0, 3).Select(i => Note(user.Id, "legacy-" + i)));
        await db.SaveChangesAsync();
    }
    var legacyMock = new MockPush();
    var legacy = new NotificationPushWorker(provider.GetRequiredService<IServiceScopeFactory>(), sender, config,
        NullLogger<NotificationPushWorker>.Instance, transport: legacyMock);
    await legacy.DrainOnceAsync(default);
    Check(legacyMock.Calls.Count == 3 && legacyMock.MaximumConcurrent == 1, "default configuration keeps sequential delivery");
    await using (var db = Open()) {
        db.Notifications.AddRange(Enumerable.Range(0, 30).Select(i => Note(user.Id, "continuous-" + i)));
        await db.SaveChangesAsync();
    }
    var loopSignal = new NotificationPushSignal();
    long waitsBeforeSecondBatch = -1;
    var loopMock = new MockPush { TargetCount = 30, AtTarget = () => waitsBeforeSecondBatch = loopSignal.WaitCount };
    using var loopWorker = new NotificationPushWorker(provider.GetRequiredService<IServiceScopeFactory>(), sender, config,
        NullLogger<NotificationPushWorker>.Instance, new() { DrainBatches = true }, loopSignal, loopMock);
    await loopWorker.StartAsync(default);
    await loopMock.TargetReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Check(waitsBeforeSecondBatch == 0, "hosted worker drains past the 25-recipient batch without entering its idle wait");
    await loopWorker.StopAsync(default);
    Console.WriteLine("PASS: isolated SQL push wakeups, rollback, bounded parallel delivery, claims, expiry, session checks, retries and legacy mode. No FCM requests were made.");
}
finally {
    if (!databaseName.StartsWith("YemenDrive_PushDrainTests_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup database");
    await using var cleanup = new YemenDriveDbContext(baseOptions);
    await cleanup.Database.EnsureDeletedAsync();
    if (File.Exists(configPath)) File.Delete(configPath);
}
static Notification Note(int userId, string title) => new() { UserId = userId, Title = title, Body = "Local mock transport only", Type = NotificationType.System };
static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); Console.WriteLine("PASS: " + description); }
sealed class MockPush : INotificationPushTransport
{
    public ConcurrentDictionary<int, int> Calls { get; } = new();
    public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockFirstWave;
    public int TargetCount;
    public Action? AtTarget;
    public TaskCompletionSource TargetReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int? FailNotification;
    private int active, maximum;
    public int MaximumConcurrent => Volatile.Read(ref maximum);
    public async Task<string> SendAsync(DevicePushInstallation installation, Notification notification, CancellationToken token)
    {
        Calls.AddOrUpdate(notification.Id, 1, (_, old) => old + 1);
        if (TargetCount > 0 && Calls.Count == TargetCount) { AtTarget?.Invoke(); TargetReached.TrySetResult(); }
        var running = Interlocked.Increment(ref active);
        int previous;
        do { previous = maximum; if (previous >= running) break; } while (Interlocked.CompareExchange(ref maximum, running, previous) != previous);
        try {
            if (BlockFirstWave) { if (running == 4) FourStarted.TrySetResult(); await Release.Task.WaitAsync(token); }
            await Task.Delay(10, token);
            if (notification.Id == FailNotification) throw new IOException("Mock provider failure");
            return "mock-sent-" + notification.Id;
        }
        finally { Interlocked.Decrement(ref active); }
    }
}
