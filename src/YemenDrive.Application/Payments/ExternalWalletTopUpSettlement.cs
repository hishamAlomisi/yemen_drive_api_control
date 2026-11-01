using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using LedgerAccount = YemenDrive.Database.Entities.LedgerAccount;
using YemenDrive.Application.Rides;

namespace YemenDrive.Application.Payments;

// Uses the same withdrawal evidence and immutable journal mechanism as ride payments.
public sealed class ExternalWalletTopUpSettlement(YemenDriveDbContext db, AccountingPostingService posting, IRideRealtimePublisher? realtime = null)
{
    public async Task ValidateAsync(int customerId, int methodId, CancellationToken token)
    {
        var wallet = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == customerId, token)
            ?? throw new ServiceException("wallet_not_found", "محفظتك غير مجهزة.");
        if (wallet.Currency != "YER") throw new ServiceException("currency_mismatch", "عملة المحفظة لا تطابق عملة الشحن.");
        await AccountsAsync(wallet.Id, methodId, token);
    }

    private async Task<(LedgerAccount Provider, LedgerAccount Wallet)> AccountsAsync(int walletId, int methodId, CancellationToken token)
    {
        async Task<LedgerAccount> Account(FinancialPartyType type, int id, LedgerAccountPurpose purpose) =>
            await db.LedgerAccounts.Include(x => x.FinancialParty).SingleOrDefaultAsync(x =>
                x.FinancialParty != null && x.FinancialParty.Type == type && x.FinancialParty.EntityId == id &&
                x.FinancialParty.IsActive && x.Purpose == purpose && x.IsPosting && x.IsActive && x.Currency == "YER", token)
            ?? throw new ServiceException("top_up_ledger_not_found", "حسابات تسوية الشحن غير مجهزة.");
        return (await Account(FinancialPartyType.ExternalProvider, methodId, LedgerAccountPurpose.ExternalWallet),
            await Account(FinancialPartyType.Wallet, walletId, LedgerAccountPurpose.CustomerWallet));
    }

    public async Task SettleAsync(PaymentWalletOperation operation, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var wallet = await db.Wallets.FromSqlInterpolated(
            $"SELECT * FROM [Wallets] WITH (UPDLOCK,HOLDLOCK) WHERE [UserId] = {operation.CustomerId}").SingleAsync(token);
        await db.Entry(wallet).ReloadAsync(token);
        await db.Entry(operation).ReloadAsync(token);
        if (operation.Status == "Succeeded") { await tx.CommitAsync(token); return; }
        if (operation.RideId != null || operation.Status != "ProviderSucceeded" || operation.ProviderSucceededAtUtc == null ||
            operation.ProviderCode is not ("Floosak" or "Jaib") || string.IsNullOrWhiteSpace(operation.ProviderReference) ||
            operation.Amount <= 0 || operation.Currency != "YER" || wallet.Currency != operation.Currency)
            throw new ServiceException("top_up_not_confirmed", "لم يؤكد المزود عملية الشحن.");
        var method = await db.PaymentMethods.SingleAsync(x => x.Id == operation.PaymentMethodId, token);
        if (method.ProviderCode != operation.ProviderCode) throw new ServiceException("provider_mismatch", "مزود الشحن يحتاج مراجعة.");
        var accounts = await AccountsAsync(wallet.Id, method.Id, token);
        var reference = $"wallet-top-up:{operation.Id}";
        var payment = new PaymentTransaction { UserId = operation.CustomerId, Amount = operation.Amount, Currency = operation.Currency,
            Provider = operation.ProviderCode, Status = PaymentStatus.Paid,
            ProviderReference = $"{operation.ProviderCode}:{operation.PaymentMethodId}:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(operation.ProviderReference))),
            IdempotencyKey = reference };
        db.PaymentTransactions.Add(payment);
        wallet.Balance += operation.Amount;
        var movement = new WalletTransaction { WalletId = wallet.Id, Type = WalletTransactionType.Credit,
            Amount = operation.Amount, BalanceAfter = wallet.Balance, ExternalReference = reference,
            Description = $"شحن المحفظة عبر {method.NameAr}" };
        db.WalletTransactions.Add(movement);
        var journal = await posting.PostAsync(new AccountingPostingRequest(JournalEntryType.WalletTopUp,
            $"TOPUP-{operation.Id}", movement.Description, operation.Currency,
            [new(accounts.Provider.Id, Debit: operation.Amount, FinancialPartyId: accounts.Provider.FinancialPartyId,
                UserId: operation.CustomerId, Description: "سحب من المحفظة الخارجية لشحن محفظة العميل"),
             new(accounts.Wallet.Id, Credit: operation.Amount, FinancialPartyId: accounts.Wallet.FinancialPartyId,
                UserId: operation.CustomerId, Description: "إضافة مبلغ الشحن إلى التزام محفظة العميل")],
            OccurredAtUtc: operation.ProviderSucceededAtUtc, SourceType: "ExternalWalletTopUp", SourceId: operation.Id.ToString(),
            IdempotencyKey: reference, CreatedByUserId: operation.CustomerId), token);
        await db.SaveChangesAsync(token);
        operation.WalletTransactionId = movement.Id;
        operation.JournalEntryId = journal.JournalEntryId;
        operation.PaymentTransactionId = payment.Id;
        operation.BalanceAfter = wallet.Balance;
        operation.PaidAtUtc = operation.ProviderSucceededAtUtc;
        operation.Status = "Succeeded";
        operation.IsOpen = false;
        operation.NextAction = "None";
        operation.ErrorCode = null;
        operation.UpdatedAtUtc = DateTime.UtcNow;
        var notification = new Notification { UserId = operation.CustomerId, Type = NotificationType.Payment,
            Title = "تم شحن محفظتك", Body = $"أضيف {operation.Amount:0.##} ريال يمني إلى محفظتك عبر {method.NameAr}.",
            DataJson = System.Text.Json.JsonSerializer.Serialize(new { action = "open_wallet_top_up", topUpId = operation.Id }) };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        if (realtime != null)
        {
            try { await realtime.PublishNotificationsCreatedAsync(
                [new NotificationRealtimeEvent(notification.UserId, notification.Id, notification.Type, notification.CreatedAtUtc)], token); }
            catch (Exception) { /* The saved notification and push outbox survive delivery interruption. */ }
        }
    }
}
