using System.Threading.Channels;
using System.Diagnostics.Metrics;
using YemenDrive.Application.Rides;

namespace YemenDrive.Api.Tracking;

public sealed record NearbyDriverVisualProfile(int ServiceKindId, int ServiceCatalogItemId, bool IsAvailable,
    string? Name, string? PhotoUrl, string? VehicleModel, string? PlateNumber, decimal Rating, string ServiceCode);
public sealed record DriverVisualUpdate(DriverLocationRealtimeEvent Location, NearbyDriverVisualProfile Profile);
public sealed record DriverVisualQueueMetrics(int Pending, int InFlight, long Coalesced, long Rejected,
    long Obsolete, long Processed, long Failed);

/// <summary>Only disposable map updates enter this bounded queue. Route receipts never do.</summary>
public sealed class DriverLocationVisualQueue : IDisposable
{
    private readonly TrackingUpgradeSettings settings;
    private readonly Meter meter = new("YemenDrive.Tracking.Visual");
    private readonly object gate = new();
    private readonly Dictionary<int, DriverVisualUpdate> pending = [];
    private readonly Dictionary<int, DriverVisualUpdate> inFlight = [];
    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private long coalesced, rejected, obsolete, sent, failed;
    public DriverLocationVisualQueue(TrackingUpgradeSettings settings)
    {
        this.settings = settings;
        meter.CreateObservableGauge("driver_visual.pending", () => Metrics.Pending);
        meter.CreateObservableGauge("driver_visual.inflight", () => Metrics.InFlight);
        meter.CreateObservableCounter("driver_visual.coalesced", () => Metrics.Coalesced);
        meter.CreateObservableCounter("driver_visual.rejected", () => Metrics.Rejected);
        meter.CreateObservableCounter("driver_visual.obsolete", () => Metrics.Obsolete);
        meter.CreateObservableCounter("driver_visual.processed", () => Metrics.Processed);
        meter.CreateObservableCounter("driver_visual.failed", () => Metrics.Failed);
    }
    public bool Enabled => settings.BufferedVisualDeliveryEnabled;
    public int Workers => Math.Clamp(settings.VisualDeliveryWorkers, 1, 16);
    public TimeSpan DeliveryTimeout => TimeSpan.FromMilliseconds(Math.Clamp(settings.VisualDeliveryTimeoutMilliseconds, 100, 10000));
    public DriverVisualQueueMetrics Metrics
    {
        get { lock (gate) return new(pending.Count, inFlight.Count, coalesced, rejected, obsolete, sent, failed); }
    }

    public bool Enqueue(DriverVisualUpdate update)
    {
        if (!Enabled) return false;
        lock (gate)
        {
            var id = update.Location.DriverId;
            var previous = pending.GetValueOrDefault(id) ?? inFlight.GetValueOrDefault(id);
            if (previous is not null && update.Location.ObservedAtUtc <= previous.Location.ObservedAtUtc)
            { obsolete++; return false; }
            if (pending.ContainsKey(id)) coalesced++;
            else if (!inFlight.ContainsKey(id) && pending.Count + inFlight.Keys.Count(key => !pending.ContainsKey(key)) >= Math.Clamp(settings.VisualQueueCapacity, 1, 10000))
            { rejected++; return false; }
            pending[id] = update;
        }
        Wake();
        return true;
    }

    public bool TryBegin(out DriverVisualUpdate update)
    {
        lock (gate)
        {
            foreach (var pair in pending)
            {
                if (inFlight.ContainsKey(pair.Key)) continue;
                update = pair.Value;
                pending.Remove(pair.Key);
                inFlight[pair.Key] = update;
                Wake(); // Allow another worker to take a different driver.
                return true;
            }
        }
        update = null!;
        return false;
    }

    public void Complete(int driverId, bool successful, bool stale = false)
    {
        lock (gate)
        {
            inFlight.Remove(driverId);
            if (stale) obsolete++;
            else if (successful) sent++;
            else failed++;
        }
        Wake();
    }
    private void Wake() => signal.Writer.TryWrite(true);
    public async Task WaitAsync(CancellationToken token) => await signal.Reader.ReadAsync(token);
    public void Dispose() { meter.Dispose(); signal.Writer.TryComplete(); }
}
