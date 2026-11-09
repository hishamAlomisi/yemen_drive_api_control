using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using YemenDrive.Api.Tracking;

static class VisualQueueScenarios
{
    public static async Task RunAsync()
    {
        var now = DateTime.UtcNow;
        DriverVisualUpdate Point(int driver, int milliseconds) => new(new(driver, null, null, 15, 44, 0, 2, 5,
            now.AddMilliseconds(milliseconds), now, null), new(1, 1, true, null, null, null, null, 0, "test"));
        using var disabled = new DriverLocationVisualQueue(new());
        Check(!disabled.Enqueue(Point(1, 0)) && !disabled.TryBegin(out _), "disabled visual delivery keeps the legacy path active");
        using var queue = new DriverLocationVisualQueue(new() { BufferedVisualDeliveryEnabled = true, VisualQueueCapacity = 2 });
        queue.Enqueue(Point(1, 0));
        Check(queue.TryBegin(out var active) && active.Location.DriverId == 1, "visual worker can reserve one driver");
        Check(!queue.Enqueue(Point(1, -1)) && !queue.Enqueue(Point(1, 0)) && queue.Metrics.Obsolete == 2,
            "older and duplicate visual observations cannot displace an in-flight sample");
        queue.Enqueue(Point(1, 1)); queue.Enqueue(Point(1, 2)); queue.Enqueue(Point(2, 0));
        Check(!queue.Enqueue(Point(3, 0)) && queue.Metrics.Rejected == 1 && queue.Metrics.Coalesced == 1,
            "visual capacity includes in-flight drivers and retains only the latest waiting sample");
        Check(queue.TryBegin(out var other) && other.Location.DriverId == 2 && !queue.TryBegin(out _),
            "a stalled driver does not prevent another driver from starting, or allow overlapping delivery for itself");
        queue.Complete(2, true); queue.Complete(1, false);
        Check(queue.TryBegin(out var latest) && latest.Location.ObservedAtUtc == now.AddMilliseconds(2),
            "after a stalled send, the next visual update is the newest rather than an obsolete FIFO");
        queue.Complete(1, true);

        var probe = new Probe(now);
        var services = new ServiceCollection().AddSingleton(probe).AddScoped<IDriverLocationVisualTransport, ProbeTransport>();
        await using var provider = services.BuildServiceProvider();
        using var bounded = new DriverLocationVisualQueue(new() { BufferedVisualDeliveryEnabled = true, VisualDeliveryWorkers = 2,
            VisualQueueCapacity = 8, VisualDeliveryTimeoutMilliseconds = 500 });
        using var worker = new DriverLocationVisualWorker(provider.GetRequiredService<IServiceScopeFactory>(), bounded,
            NullLogger<DriverLocationVisualWorker>.Instance);
        bounded.Enqueue(Point(1, 0));
        await worker.StartAsync(default);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        bounded.Enqueue(Point(1, 1)); bounded.Enqueue(Point(1, 2)); bounded.Enqueue(Point(2, 0));
        await probe.Other.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await probe.Latest.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => bounded.Metrics.InFlight == 0 && bounded.Metrics.Processed + bounded.Metrics.Failed == 3);
        await worker.StopAsync(default);
        Check(probe.OtherBeforeCancellation && probe.Cancelled && bounded.Metrics.Failed == 1 && bounded.Metrics.Processed == 2,
            "visual deadline cancels stalled transport while a different driver completes independently");
        Check(probe.Delivered.Count == 2 && probe.Delivered.Any(x => x.Driver == 1 && x.At == now.AddMilliseconds(2)) &&
            probe.Created == 3 && probe.Disposed == 3 && !probe.DisposedDuringSend,
            "scoped transports finish before disposal and coalesced intermediate points are never sent");
        Check(!probe.OverlappingDriver, "parallel visual workers never send two updates for the same driver simultaneously");

        using var oldQueue = new DriverLocationVisualQueue(new() { BufferedVisualDeliveryEnabled = true });
        using var oldWorker = new DriverLocationVisualWorker(provider.GetRequiredService<IServiceScopeFactory>(), oldQueue,
            NullLogger<DriverLocationVisualWorker>.Instance);
        oldQueue.Enqueue(Point(3, -31_000));
        await oldWorker.StartAsync(default);
        await WaitUntilAsync(() => oldQueue.Metrics.Obsolete == 1);
        await oldWorker.StopAsync(default);
        Check(probe.Created == 3 && oldQueue.Metrics.Processed == 0,
            "expired queued observations are discarded before resolving a transport scope");
    }

    private static void Check(bool valid, string label)
    {
        if (!valid) throw new InvalidOperationException(label);
        Console.WriteLine("PASS: " + label);
    }
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }
    public sealed class Probe(DateTime blockedAt)
    {
        public DateTime BlockedAt { get; } = blockedAt;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Other { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Latest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentBag<(int Driver, DateTime At)> Delivered { get; } = [];
        public ConcurrentDictionary<int, int> ActiveDrivers { get; } = new();
        public volatile bool Cancelled, OtherBeforeCancellation, DisposedDuringSend, OverlappingDriver;
        public int Created, Disposed;
    }
    public sealed class ProbeTransport : IDriverLocationVisualTransport, IDisposable
    {
        private readonly Probe probe;
        private bool active;
        public ProbeTransport(Probe probe) { this.probe = probe; Interlocked.Increment(ref probe.Created); }
        public async Task<bool> SendAsync(DriverVisualUpdate update, CancellationToken token)
        {
            active = true;
            if (probe.ActiveDrivers.AddOrUpdate(update.Location.DriverId, 1, (_, count) => count + 1) > 1)
                probe.OverlappingDriver = true;
            try
            {
                if (update.Location.DriverId == 1 && update.Location.ObservedAtUtc == probe.BlockedAt)
                {
                    probe.Entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { probe.Cancelled = true; throw; }
                }
                probe.Delivered.Add((update.Location.DriverId, update.Location.ObservedAtUtc));
                if (update.Location.DriverId == 2) { probe.OtherBeforeCancellation = !probe.Cancelled; probe.Other.TrySetResult(); }
                else probe.Latest.TrySetResult();
                return true;
            }
            finally { probe.ActiveDrivers.AddOrUpdate(update.Location.DriverId, 0, (_, count) => count - 1); active = false; }
        }
        public void Dispose() { if (active) probe.DisposedDuringSend = true; Interlocked.Increment(ref probe.Disposed); }
    }
}
