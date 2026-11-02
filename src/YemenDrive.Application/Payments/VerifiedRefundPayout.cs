using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Administration;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Payments;

/// <summary>Records an already verified value transfer. It never calls a debit or payout provider.</summary>
public static class VerifiedRefundPayout
{
    public static async Task<LedgerAccount> ValidateAsync(YemenDriveDbContext db, int actor, int accountId,
        decimal amount, string? evidence, CancellationToken ct)
    {
        await AdministratorAccess.RequireAsync(db, actor, "finance.write", ct);
        if (amount <= 0 || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 1000)
            throw new ServiceException("verified_payout_evidence_required", "سجل مرجع دليل التحويل الفعلي واستلام العميل والمبلغ الصحيح؛ لا يكفي طلب تحويل لم ينفذ.");
        var account = await db.LedgerAccounts.FromSqlInterpolated($"SELECT * FROM [LedgerAccounts] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {accountId}").SingleOrDefaultAsync(ct);
        if (account == null || !account.IsActive || !account.IsPosting || account.Type != LedgerAccountType.Asset || account.Currency != "YER" ||
            !await db.FinancialParties.AnyAsync(x => x.Id == account.FinancialPartyId && x.IsActive && (x.Type == FinancialPartyType.Bank || x.Type == FinancialPartyType.CashBox || x.Type == FinancialPartyType.ExternalProvider), ct))
            throw new ServiceException("refund_funding_account_required", "حدد حساب النقد أو البنك أو المزود الذي خرج منه المبلغ فعلياً وبعملة الاسترداد.");
        var balance = await db.JournalLines.Where(x => x.LedgerAccountId == accountId && x.JournalEntry.IsPosted && x.JournalEntry.Status == JournalEntryStatus.Posted).SumAsync(x => x.Debit - x.Credit, ct);
        if (balance < amount) throw new ServiceException("refund_funding_not_reconciled", "الرصيد المحاسبي للمصدر لا يغطي التحويل. سوِّ كشف المصدر أولاً؛ لم يسجل استرداد غير ممول.");
        return account;
    }
}
