using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Accounting;

public sealed record AccountingPostingLine(
    int LedgerAccountId,
    decimal Debit = 0,
    decimal Credit = 0,
    int? FinancialPartyId = null,
    int? UserId = null,
    int? RideId = null,
    string? Description = null);

public sealed record AccountingPostingRequest(
    JournalEntryType Type,
    string Reference,
    string Description,
    string Currency,
    IReadOnlyCollection<AccountingPostingLine> Lines,
    DateTime? OccurredAtUtc = null,
    string? SourceType = null,
    string? SourceId = null,
    string? IdempotencyKey = null,
    int? CreatedByUserId = null,
    int? ReversesJournalEntryId = null);

public sealed record AccountingPostingResult(int JournalEntryId, string EntryNumber, bool AlreadyPosted);

/// <summary>
/// The only application service allowed to publish general-ledger entries.
/// It validates both sides before adding anything and saves the header and
/// all lines inside the same serializable transaction.
/// </summary>
public sealed class AccountingPostingService(YemenDriveDbContext db)
{
    public async Task<AccountingPostingResult> PostAsync(AccountingPostingRequest request, CancellationToken token)
    {
        ValidateRequest(request);
        var currency = request.Currency.Trim().ToUpperInvariant();
        // Operational payments own one serializable transaction that also
        // changes wallets, payments and ride state. Reuse it so a journal
        // entry can never be committed while its payment rolls back.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token)
            : null;

        if (!string.IsNullOrWhiteSpace(request.SourceType) && !string.IsNullOrWhiteSpace(request.SourceId))
        {
            var existing = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x =>
                x.SourceType == request.SourceType.Trim() &&
                x.SourceId == request.SourceId.Trim() &&
                x.Type == request.Type, token);
            if (existing is not null)
            {
                if (ownsTransaction) await transaction!.CommitAsync(token);
                return new AccountingPostingResult(existing.Id, existing.EntryNumber, true);
            }
        }

        var accountIds = request.Lines.Select(x => x.LedgerAccountId).Distinct().ToArray();
        var accounts = await db.LedgerAccounts.Where(x => accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        if (accounts.Count != accountIds.Length)
            throw new ServiceException("ledger_account_not_found", "أحد الحسابات المالية غير موجود.");
        foreach (var account in accounts.Values)
        {
            if (!account.IsActive || !account.IsPosting)
                throw new ServiceException("ledger_account_not_posting", "أحد الحسابات غير نشط أو لا يقبل قيوداً مباشرة.");
            if (!string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase))
                throw new ServiceException("ledger_currency_mismatch", "عملة الحساب لا تطابق عملة القيد.");
        }

        var parties = request.Lines.Where(x => x.FinancialPartyId is not null)
            .Select(x => x.FinancialPartyId!.Value).Distinct().ToArray();
        if (parties.Length > 0)
        {
            var activeParties = await db.FinancialParties.CountAsync(x => parties.Contains(x.Id) && x.IsActive, token);
            if (activeParties != parties.Length)
                throw new ServiceException("financial_party_not_found", "إحدى الجهات المالية غير موجودة أو متوقفة.");
        }

        if (request.ReversesJournalEntryId is int reversedEntryId)
        {
            var original = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reversedEntryId, token)
                ?? throw new ServiceException("journal_entry_not_found", "القيد المراد عكسه غير موجود.");
            if (original.Status != JournalEntryStatus.Posted || !original.IsPosted)
                throw new ServiceException("journal_entry_not_posted", "لا يمكن عكس قيد غير منشور.");
            if (await db.JournalEntries.AnyAsync(x => x.ReversesJournalEntryId == reversedEntryId, token))
                throw new ServiceException("journal_entry_already_reversed", "تم إنشاء قيد عكسي لهذا القيد مسبقاً.");
        }

        var debit = request.Lines.Sum(x => x.Debit);
        var credit = request.Lines.Sum(x => x.Credit);
        var occurredAt = (request.OccurredAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        var postedAt = DateTime.UtcNow;
        var entry = new JournalEntry
        {
            EntryNumber = $"JE-{postedAt:yyyyMMdd}-{Guid.NewGuid():N}".ToUpperInvariant(),
            Reference = request.Reference.Trim(),
            Type = request.Type,
            Status = JournalEntryStatus.Posted,
            Description = request.Description.Trim(),
            Currency = currency,
            OccurredAtUtc = occurredAt,
            PostedAtUtc = postedAt,
            IsPosted = true,
            TotalDebit = debit,
            TotalCredit = credit,
            SourceType = NormalizeOptional(request.SourceType),
            SourceId = NormalizeOptional(request.SourceId),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey),
            CreatedByUserId = request.CreatedByUserId,
            PostedByUserId = request.CreatedByUserId,
            ReversesJournalEntryId = request.ReversesJournalEntryId
        };
        var lineNumber = 1;
        foreach (var line in request.Lines)
        {
            entry.Lines.Add(new JournalLine
            {
                LedgerAccountId = line.LedgerAccountId,
                Debit = line.Debit,
                Credit = line.Credit,
                Currency = currency,
                Description = string.IsNullOrWhiteSpace(line.Description) ? request.Description.Trim() : line.Description.Trim(),
                LineNumber = lineNumber++,
                FinancialPartyId = line.FinancialPartyId,
                UserId = line.UserId,
                RideId = line.RideId
            });
        }
        db.JournalEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(token);
            if (ownsTransaction) await transaction!.CommitAsync(token);
        }
        catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(request.SourceType) && !string.IsNullOrWhiteSpace(request.SourceId))
        {
            if (ownsTransaction) await transaction!.RollbackAsync(token);
            db.ChangeTracker.Clear();
            var existing = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x =>
                x.SourceType == request.SourceType!.Trim() &&
                x.SourceId == request.SourceId!.Trim() &&
                x.Type == request.Type, token);
            if (existing is not null) return new AccountingPostingResult(existing.Id, existing.EntryNumber, true);
            throw;
        }
        return new AccountingPostingResult(entry.Id, entry.EntryNumber, false);
    }

    public async Task<AccountingPostingResult> ReverseAsync(
        int journalEntryId,
        string reference,
        string description,
        int? actorUserId,
        CancellationToken token)
    {
        var original = await db.JournalEntries.AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == journalEntryId, token)
            ?? throw new ServiceException("journal_entry_not_found", "القيد المراد عكسه غير موجود.");
        if (!original.IsPosted || original.Status != JournalEntryStatus.Posted)
            throw new ServiceException("journal_entry_not_posted", "لا يمكن عكس قيد غير منشور.");
        return await PostAsync(new AccountingPostingRequest(
            JournalEntryType.Reversal,
            reference,
            description,
            original.Currency,
            original.Lines.OrderBy(x => x.LineNumber).Select(x => new AccountingPostingLine(
                x.LedgerAccountId, x.Credit, x.Debit, x.FinancialPartyId, x.UserId, x.RideId, x.Description)).ToArray(),
            SourceType: "JournalEntryReversal",
            SourceId: journalEntryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CreatedByUserId: actorUserId,
            ReversesJournalEntryId: journalEntryId), token);
    }

    private static void ValidateRequest(AccountingPostingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reference) || string.IsNullOrWhiteSpace(request.Description) ||
            string.IsNullOrWhiteSpace(request.Currency) || request.Lines.Count < 2)
            throw new ServiceException("invalid_journal_entry", "المرجع والبيان والعملة وسطران متوازنان على الأقل مطلوبة.");
        if (request.Reference.Trim().Length > 160 || request.Description.Trim().Length > 1000 || request.Currency.Trim().Length > 12)
            throw new ServiceException("invalid_journal_entry", "إحدى بيانات القيد أطول من المسموح.");
        foreach (var line in request.Lines)
        {
            var hasDebit = line.Debit > 0 && line.Credit == 0;
            var hasCredit = line.Credit > 0 && line.Debit == 0;
            if (line.LedgerAccountId <= 0 || (!hasDebit && !hasCredit))
                throw new ServiceException("invalid_journal_line", "كل سطر يحتاج حساباً وطرفاً واحداً موجباً: مدين أو دائن.");
        }
        var debit = request.Lines.Sum(x => x.Debit);
        var credit = request.Lines.Sum(x => x.Credit);
        if (debit <= 0 || debit != credit)
            throw new ServiceException("journal_entry_unbalanced", "إجمالي المدين يجب أن يساوي إجمالي الدائن وأن يكون أكبر من صفر.");
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
