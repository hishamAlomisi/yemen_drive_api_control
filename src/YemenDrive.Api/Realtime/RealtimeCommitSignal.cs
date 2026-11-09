using System.Data.Common;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YemenDrive.Database;

namespace YemenDrive.Api.Realtime;

public sealed class RealtimeCommitSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Wake() => channel.Writer.TryWrite(true);
    public async Task WaitAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { await channel.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* restart/missed-wakeup recovery only */ }
    }
}

public sealed class RealtimeCommitInterceptor(RealtimeCommitSignal signal, NotificationPushSignal? pushSignal = null) : DbTransactionInterceptor
{
    private void Committed(TransactionEndEventData data)
    {
        if (data.Context is YemenDriveDbContext db) {
            if (db.ConsumeRealtimeCommitSignal()) signal.Wake();
            if (db.ConsumeNotificationPushSignal()) pushSignal?.Wake();
        }
    }
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(eventData);
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { Committed(eventData); return Task.CompletedTask; }
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    { if (eventData.Context is YemenDriveDbContext db) { db.ConsumeRealtimeCommitSignal(); db.ConsumeNotificationPushSignal(); } }
    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { TransactionRolledBack(transaction, eventData); return Task.CompletedTask; }
}
