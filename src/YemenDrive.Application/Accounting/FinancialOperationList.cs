using System.Globalization;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.Accounting;

public sealed record FinancialOperationListQuery(DateOnly? OperationDate = null);

public sealed class FinancialOperationList(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ModelService<FinancialOperationListQuery>(config)
{
    public override string ModelName => nameof(FinancialOperationModel);
    public override IReadOnlyCollection<string> Operations { get; } = ["list"];

    protected override async Task<object?> ExecuteOperationAsync(
        string operation,
        FinancialOperationListQuery model,
        CancellationToken cancellationToken)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, cancellationToken);

        var query = db.JournalEntries.AsNoTracking()
            .Where(x => x.SourceType == "AdminFinancialOperation" && x.IsPosted &&
                x.Status == JournalEntryStatus.Posted &&
                (x.Type == JournalEntryType.AdminCashReceipt ||
                 x.Type == JournalEntryType.AdminCashDisbursement ||
                 x.Type == JournalEntryType.AdminAccountTransfer));

        if (model.OperationDate is DateOnly date)
        {
            var from = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var until = from.AddDays(1);
            query = query.Where(x => x.OccurredAtUtc >= from && x.OccurredAtUtc < until);
        }

        var entries = await query
            .Include(x => x.Lines)
            .ThenInclude(x => x.LedgerAccount)
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenByDescending(x => x.Id)
            .ToArrayAsync(cancellationToken);
        if (entries.Length == 0) return Array.Empty<object>();

        var entryIds = entries.Select(x => x.Id).ToArray();
        var reversals = await db.JournalEntries.AsNoTracking()
            .Where(x => x.ReversesJournalEntryId != null && entryIds.Contains(x.ReversesJournalEntryId.Value))
            .Select(x => new { OriginalId = x.ReversesJournalEntryId!.Value, x.Description })
            .ToDictionaryAsync(x => x.OriginalId, x => x.Description, cancellationToken);

        return entries.Select(entry =>
        {
            var debit = entry.Lines.Single(x => x.Debit > 0);
            var credit = entry.Lines.Single(x => x.Credit > 0);
            var operationType = entry.Type switch
            {
                JournalEntryType.AdminCashReceipt => "cashReceipt",
                JournalEntryType.AdminCashDisbursement => "cashDisbursement",
                _ => "accountTransfer"
            };
            var operationName = operationType switch
            {
                "cashReceipt" => "قبض نقدي",
                "cashDisbursement" => "صرف نقدي",
                _ => "قيد مالي"
            };
            var prefix = operationName + ": ";
            var note = entry.Description.StartsWith(prefix, StringComparison.Ordinal)
                ? entry.Description[prefix.Length..]
                : entry.Description;
            var reversed = reversals.TryGetValue(entry.Id, out var reversalDescription);
            var wasEdited = reversed && reversalDescription!.StartsWith(
                "عكس العملية المالية بسبب التعديل:", StringComparison.Ordinal);

            return new
            {
                journalEntryId = entry.Id,
                operationType,
                operationName,
                entryNumber = entry.EntryNumber,
                operationDate = DateOnly.FromDateTime(entry.OccurredAtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                debitAccountId = debit.LedgerAccountId,
                debitAccount = $"{debit.LedgerAccount.Code} — {debit.LedgerAccount.Name}",
                creditAccountId = credit.LedgerAccountId,
                creditAccount = $"{credit.LedgerAccount.Code} — {credit.LedgerAccount.Name}",
                amount = entry.TotalDebit,
                currency = entry.Currency,
                note,
                status = reversed ? (wasEdited ? "تم تعديلها" : "ملغاة بقيد عكسي") : "مرحل",
                canModify = !reversed
            };
        }).ToArray();
    }
}
