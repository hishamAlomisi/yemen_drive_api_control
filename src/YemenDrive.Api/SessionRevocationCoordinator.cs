using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace YemenDrive.Api;

/// <summary>Never closes a socket for access changes that have not committed.</summary>
public sealed class SessionRevocationCoordinator(TrackingConnectionRegistry connections)
{
    internal sealed class Batch
    {
        public HashSet<int> Users { get; } = [];
        public HashSet<Guid> Families { get; } = [];
        public void Add(Batch other) { Users.UnionWith(other.Users); Families.UnionWith(other.Families); }
    }
    private sealed class ContextState
    {
        public Dictionary<Guid, Batch> Transactions { get; } = [];
        public Batch? Saving { get; set; }
        public Guid SavingTransaction { get; set; }
    }
    private readonly ConditionalWeakTable<DbContext, ContextState> contexts = new();

    public void RevokeUserAfterCommit(DbContext db, int userId) { var batch = new Batch(); batch.Users.Add(userId); Stage(db, batch); }
    public void RevokeFamilyAfterCommit(DbContext db, Guid familyId) { var batch = new Batch(); batch.Families.Add(familyId); Stage(db, batch); }
    private void Stage(DbContext db, Batch batch)
    {
        if (db.Database.CurrentTransaction is not { } transaction) { Apply(batch); return; }
        var state = contexts.GetValue(db, _ => new ContextState());
        if (!state.Transactions.TryGetValue(transaction.TransactionId, out var pending))
            state.Transactions[transaction.TransactionId] = pending = new();
        pending.Add(batch);
    }

    internal void BeginSave(DbContext db, Batch batch)
    {
        var state = contexts.GetValue(db, _ => new ContextState());
        state.Saving = batch;
        state.SavingTransaction = db.Database.CurrentTransaction?.TransactionId ?? Guid.Empty;
    }
    internal void Saved(DbContext db)
    {
        if (!contexts.TryGetValue(db, out var state) || state.Saving is not { } saved) return;
        state.Saving = null;
        Stage(db, saved);
    }
    internal void SaveFailed(DbContext db) { if (contexts.TryGetValue(db, out var state)) state.Saving = null; }
    internal void TransactionEnded(DbContext db, Guid id, bool committed)
    {
        if (!contexts.TryGetValue(db, out var state)) return;
        var batch = new Batch();
        if (state.Transactions.Remove(id, out var staged)) batch.Add(staged);
        if (state.Saving is not null && (state.SavingTransaction == id || state.SavingTransaction == Guid.Empty))
        { batch.Add(state.Saving); state.Saving = null; }
        if (committed) Apply(batch);
    }
    private void Apply(Batch batch)
    {
        // Abort before any durable delivery wakeup. No network write is awaited
        // here, and a malicious client cannot postpone revocation by ignoring it.
        foreach (var user in batch.Users) connections.RevokeUser(user);
        foreach (var family in batch.Families) connections.RevokeFamily(family);
    }
}

public sealed class SessionRevocationTransactionInterceptor(SessionRevocationCoordinator coordinator) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData data)
    { if (data.Context is { } db) coordinator.TransactionEnded(db, data.TransactionId, true); }
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
    { TransactionCommitted(transaction, data); return Task.CompletedTask; }
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData data)
    { if (data.Context is { } db) coordinator.TransactionEnded(db, data.TransactionId, false); }
    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
    { TransactionRolledBack(transaction, data); return Task.CompletedTask; }
}
