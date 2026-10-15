using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Accounting;

public sealed class FinancialOperation(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser,
    AccountingPostingService accounting) : OperationsService<FinancialOperationModel>(config)
{
    protected override async Task<object?> AddAsync(FinancialOperationModel model, CancellationToken token)
    {
        var adminId = await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var result = await PostOperationAsync(model, adminId, token);
        await transaction.CommitAsync(token);
        return result;
    }

    protected override async Task<object?> UpdateAsync(FinancialOperationModel model, CancellationToken token)
    {
        var adminId = await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var original = await FindEditableEntryAsync(model.JournalEntryId, token);

        var reversal = await accounting.ReverseAsync(
            original.Id,
            $"FIN-EDIT-{original.Id}-{Guid.NewGuid():N}",
            $"عكس العملية المالية بسبب التعديل: {original.Description}",
            adminId,
            token);
        var replacement = await PostOperationAsync(model, adminId, token);
        await transaction.CommitAsync(token);

        return new
        {
            originalEntryNumber = original.EntryNumber,
            reversalEntryNumber = reversal.EntryNumber,
            replacement
        };
    }

    protected override async Task<object?> DeleteAsync(FinancialOperationModel model, CancellationToken token)
    {
        var adminId = await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var original = await FindEditableEntryAsync(model.JournalEntryId, token);
        var reversal = await accounting.ReverseAsync(
            original.Id,
            $"FIN-DELETE-{original.Id}-{Guid.NewGuid():N}",
            $"إلغاء العملية المالية من لوحة الإدارة: {original.Description}",
            adminId,
            token);
        await transaction.CommitAsync(token);

        return new { originalEntryNumber = original.EntryNumber, reversalEntryNumber = reversal.EntryNumber };
    }

    private async Task<JournalEntry> FindEditableEntryAsync(int journalEntryId, CancellationToken token)
    {
        if (journalEntryId <= 0)
            throw new ServiceException("journal_entry_required", "حدد سجل العملية المالية المطلوب.");

        var entry = await db.JournalEntries.AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == journalEntryId, token)
            ?? throw new ServiceException("financial_operation_not_found", "سجل العملية المالية غير موجود.");

        if (entry.SourceType != "AdminFinancialOperation" ||
            entry.Type is not (JournalEntryType.AdminCashReceipt or
                JournalEntryType.AdminCashDisbursement or JournalEntryType.AdminAccountTransfer) ||
            !entry.IsPosted || entry.Status != JournalEntryStatus.Posted)
            throw new ServiceException("financial_operation_not_editable", "هذا السجل ليس عملية مالية إدارية قابلة للتعديل أو الإلغاء.");

        if (await db.JournalEntries.AsNoTracking().AnyAsync(x => x.ReversesJournalEntryId == entry.Id, token))
            throw new ServiceException("financial_operation_already_closed", "سبق تعديل هذه العملية أو إلغاؤها؛ راجع القيد الناتج عنها.");

        return entry;
    }

    private async Task<object> PostOperationAsync(
        FinancialOperationModel model,
        int adminId,
        CancellationToken token)
    {
        var operationType = model.OperationType.Trim();
        var (entryType, label) = operationType switch
        {
            "cashReceipt" => (JournalEntryType.AdminCashReceipt, "قبض نقدي"),
            "cashDisbursement" => (JournalEntryType.AdminCashDisbursement, "صرف نقدي"),
            "accountTransfer" => (JournalEntryType.AdminAccountTransfer, "قيد مالي"),
            _ => throw new ServiceException("invalid_financial_operation", "نوع العملية المالية غير معروف.")
        };

        if (model.DebitAccountId <= 0 || model.CreditAccountId <= 0 ||
            model.DebitAccountId == model.CreditAccountId)
            throw new ServiceException("invalid_financial_accounts", "اختر حسابين مختلفين للمدين والدائن.");
        if (model.Amount <= 0 || model.Amount > 9999999999999999m || decimal.Round(model.Amount, 2) != model.Amount)
            throw new ServiceException("invalid_financial_amount", "أدخل مبلغاً موجباً لا يتجاوز منزلتين عشريتين.");
        if (model.OperationDate == default || model.OperationDate.Year < 2000 || model.OperationDate.Year > 9999)
            throw new ServiceException("invalid_financial_date", "تاريخ العملية غير صالح.");
        var note = model.Note.Trim();
        if (string.IsNullOrWhiteSpace(note) || note.Length > 900)
            throw new ServiceException("invalid_financial_note", "الملاحظة مطلوبة ويجب ألا تتجاوز 900 حرف.");

        var accountIds = new[] { model.DebitAccountId, model.CreditAccountId };
        var accounts = await db.LedgerAccounts.AsNoTracking()
            .Where(x => accountIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, token);
        if (accounts.Count != 2)
            throw new ServiceException("ledger_account_not_found", "أحد الحسابين الماليين غير موجود.");

        var debitAccount = accounts[model.DebitAccountId];
        var creditAccount = accounts[model.CreditAccountId];
        if (!debitAccount.IsActive || !debitAccount.IsPosting || !creditAccount.IsActive || !creditAccount.IsPosting)
            throw new ServiceException("ledger_account_not_posting", "اختر حسابين نشطين يقبلان تسجيل القيود مباشرة.");
        if (!string.Equals(debitAccount.Currency, creditAccount.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException("ledger_currency_mismatch", "يجب أن تكون عملة الحساب المدين مطابقة لعملة الحساب الدائن.");

        if (operationType is "cashReceipt" or "cashDisbursement")
        {
            var cashRoot = await db.LedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Code == "1000" && x.Purpose == LedgerAccountPurpose.CashCollection &&
                x.Currency == debitAccount.Currency && x.IsActive, token);
            if (cashRoot is null)
                throw new ServiceException("cash_account_not_configured", "حساب الصندوق النقدي للإدارة غير مهيأ لهذه العملة.");

            var hierarchy = await db.LedgerAccounts.AsNoTracking()
                .Select(x => new { x.Id, x.ParentLedgerAccountId })
                .ToDictionaryAsync(x => x.Id, x => x.ParentLedgerAccountId, token);
            var cashAccountId = operationType == "cashReceipt" ? debitAccount.Id : creditAccount.Id;
            if (!IsWithinCashAccount(cashAccountId, cashRoot.Id, hierarchy))
                throw new ServiceException("cash_account_required", "يجب أن يكون طرف النقدية حساباً تابعاً للصندوق النقدي للإدارة.");
        }

        var occurredAtUtc = DateTime.SpecifyKind(
            model.OperationDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var description = $"{label}: {note}";
        var result = await accounting.PostAsync(new AccountingPostingRequest(
            entryType,
            $"ADM-{entryType.ToString().ToUpperInvariant()}-{Guid.NewGuid():N}",
            description,
            debitAccount.Currency,
            [
                new AccountingPostingLine(debitAccount.Id, Debit: model.Amount, Description: description),
                new AccountingPostingLine(creditAccount.Id, Credit: model.Amount, Description: description)
            ],
            OccurredAtUtc: occurredAtUtc,
            SourceType: "AdminFinancialOperation",
            SourceId: Guid.NewGuid().ToString("N"),
            CreatedByUserId: adminId), token);

        return new
        {
            result.JournalEntryId,
            result.EntryNumber,
            operationType,
            debitAccount = $"{debitAccount.Code} — {debitAccount.Name}",
            creditAccount = $"{creditAccount.Code} — {creditAccount.Name}",
            model.Amount,
            currency = debitAccount.Currency,
            model.OperationDate,
            note
        };
    }

    private static bool IsWithinCashAccount(
        int accountId,
        int cashRootId,
        IReadOnlyDictionary<int, int?> parentById)
    {
        var visited = new HashSet<int>();
        var currentId = accountId;
        while (visited.Add(currentId) && parentById.TryGetValue(currentId, out var parentId))
        {
            if (currentId == cashRootId) return true;
            if (parentId is not int parent) return false;
            currentId = parent;
        }
        return false;
    }
}
