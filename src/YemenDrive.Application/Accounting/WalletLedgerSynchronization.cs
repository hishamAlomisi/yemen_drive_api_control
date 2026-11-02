using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Accounting;

public sealed class WalletLedgerSynchronization(YemenDriveDbContext db)
{
    public List<string> ReviewMessages { get; } = [];
    private static bool Direct(JournalEntry entry) => entry.SourceType is "AdminFinancialOperation" or "AdminJournalEntry" or "AdminOpeningBalance";
    public async Task ApplyPostedAsync(JournalEntry entry, CancellationToken ct)
    {
        if (!await IsAdministrativeAsync(entry, ct)) return;
        await ApplyBatchAsync([entry], ct);
    }
    public async Task ApplyAdministrativeBatchAsync(IEnumerable<JournalEntry> entries, CancellationToken ct) {
        var manual = new List<JournalEntry>();
        foreach (var entry in entries.DistinctBy(x => x.Id)) if (await IsAdministrativeAsync(entry, ct)) manual.Add(entry);
        await ApplyBatchAsync(manual, ct);
    }
    private async Task<bool> IsAdministrativeAsync(JournalEntry entry, CancellationToken ct)
    {
        var visited = new HashSet<int>();
        while (visited.Add(entry.Id)) {
            if (Direct(entry)) return true;
            if (entry.ReversesJournalEntryId is not int originalId) return false;
            entry = await db.JournalEntries.AsNoTracking().SingleAsync(x => x.Id == originalId, ct);
        }
        return false;
    }
    public async Task<int> ReconcileExistingAsync(CancellationToken ct, IReadOnlySet<int>? allowDebtWallets = null, int? approvedByUserId = null, string? approvalReason = null)
    {
        ReviewMessages.Clear();
        var candidates = await db.JournalEntries.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.IsPosted && x.Status == JournalEntryStatus.Posted &&
                (x.SourceType == "AdminFinancialOperation" || x.SourceType == "AdminJournalEntry" || x.SourceType == "AdminOpeningBalance" || x.ReversesJournalEntryId != null))
            .OrderBy(x => x.Id).ToListAsync(ct);
        var manual = new List<JournalEntry>();
        foreach (var item in candidates) if (await IsAdministrativeAsync(item, ct)) manual.Add(item);
        var accountIds = manual.SelectMany(x => x.Lines).Select(x => x.LedgerAccountId).Distinct().ToArray();
        var accountWallets = await db.LedgerAccounts.AsNoTracking().Where(x => accountIds.Contains(x.Id) && x.FinancialParty != null && x.FinancialParty.Type == FinancialPartyType.Wallet && x.FinancialParty.EntityId != null)
            .Select(x => new { x.Id, WalletId = x.FinancialParty!.EntityId!.Value }).ToDictionaryAsync(x => x.Id, x => x.WalletId, ct);
        var applications = await db.WalletLedgerApplications.AsNoTracking().Select(x => new { x.JournalEntryId, x.WalletId }).ToListAsync(ct);
        var applied = applications.Select(x => (x.JournalEntryId, x.WalletId)).ToHashSet();
        var parent = new Dictionary<int, int>();
        int Root(int id) { if (!parent.ContainsKey(id)) parent[id] = id; return parent[id] == id ? id : parent[id] = Root(parent[id]); }
        var pending = new List<(JournalEntry Entry, int[] Wallets)>();
        foreach (var entry in manual) {
            var walletIds = entry.Lines.Where(x => accountWallets.ContainsKey(x.LedgerAccountId)).Select(x => accountWallets[x.LedgerAccountId]).Distinct().Where(id => !applied.Contains((entry.Id, id))).ToArray();
            if (walletIds.Length == 0) continue;
            foreach (var id in walletIds) parent[Root(id)] = Root(walletIds[0]);
            pending.Add((entry, walletIds));
        }
        var count = 0;
        // Keep both sides of an old wallet transfer together, while an invalid debit on an unrelated wallet does not block valid deposits.
        foreach (var group in pending.GroupBy(x => Root(x.Wallets[0])).ToArray()) {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try {
                var appliedCount = await ApplyBatchAsync(group.Select(x => x.Entry).ToArray(), ct, allowDebtWallets, approvedByUserId, approvalReason);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                count += appliedCount;
            } catch (ServiceException error) {
                await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                ReviewMessages.Add($"Wallet group {group.Key}: {error.Code} — {error.Message}");
            }
        }
        return count;
    }
    private async Task<int> ApplyBatchAsync(IReadOnlyCollection<JournalEntry> entries, CancellationToken ct, IReadOnlySet<int>? allowDebtWallets = null, int? approvedByUserId = null, string? approvalReason = null)
    {
        if (entries.Count == 0) return 0;
        var ids = entries.SelectMany(x => x.Lines).Select(x => x.LedgerAccountId).Distinct().ToArray();
        var accounts = await db.LedgerAccounts.Include(x => x.FinancialParty).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var effects = new List<(JournalEntry Entry, int WalletId, decimal Delta)>();
        foreach (var entry in entries) {
            var grouped = new Dictionary<int, decimal>();
            foreach (var line in entry.Lines) {
                var account = accounts[line.LedgerAccountId];
                if (account.Purpose == LedgerAccountPurpose.CustomerWallet && account.IsPosting && account.FinancialParty?.Type != FinancialPartyType.Wallet)
                    throw new ServiceException("wallet_ledger_not_linked", "حساب المحفظة غير مربوط بمحفظة فعلية. راجع تهيئة الحسابات.");
                if (account.FinancialParty?.Type != FinancialPartyType.Wallet) continue;
                if (account.Type != LedgerAccountType.Liability || account.Currency != entry.Currency)
                    throw new ServiceException("wallet_ledger_invalid", "حساب المحفظة يجب أن يكون التزاماً بعملة المحفظة نفسها.");
                var walletId = account.FinancialParty.EntityId ?? throw new ServiceException("wallet_ledger_not_linked", "حساب المحفظة غير مربوط بمحفظة فعلية.");
                grouped[walletId] = grouped.GetValueOrDefault(walletId) + line.Credit - line.Debit;
            }
            foreach (var pair in grouped) {
                if (!await db.WalletLedgerApplications.AnyAsync(x => x.JournalEntryId == entry.Id && x.WalletId == pair.Key, ct)) effects.Add((entry, pair.Key, pair.Value));
            }
        }
        var walletIds = effects.Select(x => x.WalletId).Distinct().ToArray();
        var wallets = await db.Wallets.Where(x => walletIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        foreach (var group in effects.GroupBy(x => x.WalletId)) {
            if (!wallets.TryGetValue(group.Key, out var wallet) || group.Any(x => x.Entry.Currency != wallet.Currency))
                throw new ServiceException("wallet_ledger_not_linked", "المحفظة المرتبطة بالقيد غير موجودة أو عملتها غير متوافقة.");
            var netBefore = wallet.Balance - wallet.LedgerDebt;
            var netAfter = netBefore + group.Sum(x => x.Delta);
            if (netAfter < 0 && netAfter < netBefore && allowDebtWallets?.Contains(wallet.Id) != true)
                throw new ServiceException("wallet_insufficient_balance", "رصيد المحفظة لا يسمح بالقيد أو الصرف أو عكس العملية. لم تُنفّذ العملية.");
        }
        foreach (var effect in effects.OrderBy(x => x.Delta < 0).ThenBy(x => x.Entry.Id)) {
            var wallet = wallets[effect.WalletId];
            var netAfter = wallet.Balance - wallet.LedgerDebt + effect.Delta;
            wallet.Balance = Math.Max(0, netAfter); wallet.LedgerDebt = Math.Max(0, -netAfter); wallet.UpdatedAtUtc = DateTime.UtcNow;
            db.WalletLedgerApplications.Add(new() { JournalEntryId = effect.Entry.Id, WalletId = wallet.Id, Delta = effect.Delta,
                DebtApprovedByUserId = allowDebtWallets?.Contains(wallet.Id) == true ? approvedByUserId : null,
                DebtApprovalReason = allowDebtWallets?.Contains(wallet.Id) == true ? approvalReason : null });
            if (effect.Delta == 0) continue;
            db.WalletTransactions.Add(new() { WalletId = wallet.Id, Type = effect.Delta > 0 ? WalletTransactionType.Credit : WalletTransactionType.Debit,
                Amount = Math.Abs(effect.Delta), BalanceAfter = netAfter, ExternalReference = $"wallet-journal:{effect.Entry.Id}", Description = effect.Entry.Description });
        }
        return effects.Count;
    }
}
