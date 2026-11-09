using System.Threading.Channels;
using YemenDrive.Database.Entities;

namespace YemenDrive.Api;

public sealed class NotificationPushSettings
{
    public bool DrainBatches { get; init; }
}

/// <summary>Coalesced notification wakeups; the SQL outbox remains authoritative.</summary>
public sealed class NotificationPushSignal
{
    private readonly Channel<bool> wakeups = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite });
    private long wakeCount;
    private long waitCount;
    public long WakeCount => Interlocked.Read(ref wakeCount);
    public long WaitCount => Interlocked.Read(ref waitCount);
    public void Wake()
    {
        Interlocked.Increment(ref wakeCount);
        wakeups.Writer.TryWrite(true);
    }
    public async Task WaitAsync(CancellationToken token)
    {
        Interlocked.Increment(ref waitCount);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await wakeups.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* restart, retries, or missed wakeup */ }
    }
}

/// <summary>Optional test transport. Production uses the existing Firebase sender.</summary>
public interface INotificationPushTransport
{
    Task<string> SendAsync(DevicePushInstallation installation, Notification notification, CancellationToken token);
}
