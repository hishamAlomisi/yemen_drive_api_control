using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using TransferEntity = YemenDrive.Database.Entities.WalletTransfer;

namespace YemenDrive.Application.Wallets;

public sealed record WalletTransferModel(Guid? Reference = null, Guid? RequestId = null, string PhoneNumber = "",
    decimal Amount = 0, string Note = "", string Password = "");

public sealed class WalletTransfer(YemenDriveDbContext db, DatabaseConfigurationStore config, ICurrentUserContext actor)
    : ModelService<WalletTransferModel>(config)
{
    public override IReadOnlyCollection<string> Operations => ["quote", "confirm", "get", "list"];
    protected override async Task<object?> ExecuteOperationAsync(string operation, WalletTransferModel m, CancellationToken ct)
    {
        var userId = actor.RequireUserId();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.IsActive && x.Role == UserRole.Customer, ct)
            ?? throw new ServiceException("transfer_sender_not_allowed", "التحويل متاح من حساب عميل نشط فقط.");
        if (operation == "list") return (await db.WalletTransfers.AsNoTracking().Where(x => x.SenderUserId == userId && x.CompletedAtUtc != null)
            .OrderByDescending(x => x.CompletedAtUtc).Take(100).ToListAsync(ct)).Select(View).ToArray();
        if (operation == "get") return View(await FindAsync(m.Reference, userId, ct));
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.Entry(user).ReloadAsync(ct);
        if (!user.IsActive || user.Role != UserRole.Customer)
            throw new ServiceException("transfer_sender_not_allowed", "التحويل متاح من حساب عميل نشط فقط.");
        var sender = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == userId, ct)
            ?? throw new ServiceException("wallet_not_found", "محفظتك غير موجودة.");
        if (operation == "quote")
        {
            if (m.RequestId is not Guid request || request == Guid.Empty || m.Amount <= 0 || m.Amount > 1_000_000_000 || decimal.Round(m.Amount, 2) != m.Amount || m.Note.Length > 500)
                throw new ServiceException("invalid_transfer", "أدخل مبلغاً صحيحاً بمنزلتين عشريتين كحد أقصى وملاحظة لا تتجاوز 500 حرف.");
            var existing = await db.WalletTransfers.SingleOrDefaultAsync(x => x.SenderUserId == userId && x.RequestId == request, ct);
            if (existing != null)
            {
                if (existing.Amount != m.Amount || existing.Note != m.Note.Trim() || CanonicalPhone(existing.RecipientPhone) != CanonicalPhone(m.PhoneNumber))
                    throw new ServiceException("transfer_request_mismatch", "مرجع الطلب مستخدم لبيانات تحويل مختلفة.");
                return View(existing);
            }
            var rule = await db.WalletTransferFeeRules.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive && x.FromAmount <= m.Amount && (x.ToAmount == null || m.Amount < x.ToAmount), ct)
                ?? throw new ServiceException("transfer_fee_unavailable", "التحويل غير متاح لهذا المبلغ حالياً. يرجى مراجعة الدعم.");
            var fee = decimal.Round(rule.FeeKind == "Percent" ? m.Amount * rule.FeeValue / 100 : rule.FeeValue, 2, MidpointRounding.AwayFromZero);
            if (sender.Balance < m.Amount + fee) throw Insufficient();
            var phone = CanonicalPhone(m.PhoneNumber);
            var variants = new[] { phone, "967" + phone, "+967" + phone, "00967" + phone, "0" + phone };
            var recipients = await db.Users.Include(x => x.Wallet).Where(x => variants.Contains(x.PhoneNumber) && x.IsActive && (x.Role == UserRole.Customer || x.Role == UserRole.Driver) && x.Wallet != null).ToListAsync(ct);
            if (recipients.Count != 1) throw new ServiceException("recipient_not_found", "لا توجد محفظة متاحة بهذا الرقم، أو يحتاج الرقم إلى مراجعة الدعم.");
            var recipient = recipients[0];
            if (recipient.Id == userId) throw new ServiceException("transfer_to_self", "لا يمكنك التحويل إلى محفظتك نفسها.");
            if (sender.Currency != recipient.Wallet!.Currency || sender.Currency != "YER") throw new ServiceException("transfer_currency_mismatch", "عملتا المحفظتين غير متوافقتين.");
            var row = new TransferEntity { RequestId = request, SenderUserId = userId, RecipientUserId = recipient.Id,
                RecipientName = string.IsNullOrWhiteSpace(recipient.DisplayName) ? recipient.PhoneNumber : recipient.DisplayName,
                RecipientPhone = recipient.PhoneNumber, RecipientRole = recipient.Role, Amount = m.Amount, Fee = fee,
                Currency = sender.Currency, Note = m.Note.Trim(), FeeRuleId = rule.Id, FeeKind = rule.FeeKind, FeeValue = rule.FeeValue,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) };
            db.WalletTransfers.Add(row); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return View(row);
        }
        var transfer = await FindAsync(m.Reference, userId, ct);
        if (transfer.CompletedAtUtc != null) return View(transfer);
        if (transfer.ExpiresAtUtc <= DateTime.UtcNow) throw new ServiceException("transfer_quote_expired", "انتهت صلاحية التأكيد. ارجع وأعد المتابعة لعرض الرسوم الحالية.");
        if (sender.TransferLockedUntilUtc > DateTime.UtcNow) throw new ServiceException("transfer_password_locked", "توقفت محاولات تأكيد التحويل مؤقتاً. أعد المحاولة بعد 15 دقيقة.");
        if (string.IsNullOrEmpty(m.Password) || m.Password.Length > 256 || !PasswordHash.Verify(m.Password, user.PasswordHash))
        {
            sender.TransferPasswordFailures++;
            if (sender.TransferPasswordFailures >= 5) { sender.TransferPasswordFailures = 0; sender.TransferLockedUntilUtc = DateTime.UtcNow.AddMinutes(15); }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            throw new ServiceException("transfer_password_invalid", "كلمة المرور غير صحيحة. لم يتم التحويل.");
        }
        var target = await db.Users.Include(x => x.Wallet).SingleOrDefaultAsync(x => x.Id == transfer.RecipientUserId && x.IsActive && (x.Role == UserRole.Customer || x.Role == UserRole.Driver), ct);
        if (target?.Wallet == null || target.Wallet.Currency != sender.Currency || target.PhoneNumber != transfer.RecipientPhone || target.Role != transfer.RecipientRole)
            throw new ServiceException("recipient_changed", "تغيرت بيانات المستلم. أعد المتابعة للتحقق من المحفظة.");
        if (sender.Balance < transfer.Amount + transfer.Fee) throw Insufficient();
        var provision = new FinancialAccountProvisioningService(db);
        await provision.ProvisionUserAsync(user, ct); await provision.ProvisionUserAsync(target, ct);
        await db.SaveChangesAsync(ct);
        var accounts = await db.LedgerAccounts.Where(x => x.Code == $"2100-WLT-{sender.Id}" || x.Code == $"2100-WLT-{target.Wallet.Id}" || x.Code == "4110").ToListAsync(ct);
        var fromAccount = accounts.Single(x => x.Code == $"2100-WLT-{sender.Id}");
        var toAccount = accounts.Single(x => x.Code == $"2100-WLT-{target.Wallet.Id}");
        var reference = $"wallet-transfer:{transfer.Reference:D}";
        sender.Balance -= transfer.Amount + transfer.Fee; target.Wallet.Balance += transfer.Amount;
        sender.TransferPasswordFailures = 0; sender.TransferLockedUntilUtc = null;
        sender.UpdatedAtUtc = target.Wallet.UpdatedAtUtc = DateTime.UtcNow;
        db.WalletTransactions.Add(new() { WalletId = sender.Id, Type = WalletTransactionType.Debit, Amount = transfer.Amount + transfer.Fee, BalanceAfter = sender.Balance,
            Description = $"تحويل إلى {transfer.RecipientName} ({transfer.RecipientPhone})، المبلغ {transfer.Amount} والرسوم {transfer.Fee}. {transfer.Note}", ExternalReference = reference });
        db.WalletTransactions.Add(new() { WalletId = target.Wallet.Id, Type = WalletTransactionType.Credit, Amount = transfer.Amount, BalanceAfter = target.Wallet.Balance,
            Description = $"تحويل من {user.DisplayName ?? user.PhoneNumber} ({user.PhoneNumber}). {transfer.Note}", ExternalReference = reference });
        var lines = new List<AccountingPostingLine> {
            new(fromAccount.Id, Debit: transfer.Amount + transfer.Fee, FinancialPartyId: fromAccount.FinancialPartyId, UserId: userId),
            new(toAccount.Id, Credit: transfer.Amount, FinancialPartyId: toAccount.FinancialPartyId, UserId: target.Id) };
        if (transfer.Fee > 0) lines.Add(new(accounts.Single(x => x.Code == "4110").Id, Credit: transfer.Fee, UserId: userId));
        var posted = await new AccountingPostingService(db).PostAsync(new(JournalEntryType.WalletTransfer, reference, "تحويل بين محفظتين ورسوم التحويل", transfer.Currency, lines,
            SourceType: "WalletTransfer", SourceId: transfer.Reference.ToString("D"), IdempotencyKey: reference, CreatedByUserId: userId), ct);
        transfer.JournalEntryId = posted.JournalEntryId; transfer.CompletedAtUtc = DateTime.UtcNow;
        transfer.SenderBalanceAfter = sender.Balance; transfer.RecipientBalanceAfter = target.Wallet.Balance;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return View(transfer);
    }
    private async Task<TransferEntity> FindAsync(Guid? reference, int userId, CancellationToken ct) =>
        await db.WalletTransfers.SingleOrDefaultAsync(x => x.Reference == reference && x.SenderUserId == userId, ct)
        ?? throw new ServiceException("transfer_not_found", "التحويل غير موجود.");
    private static ServiceException Insufficient() => new("transfer_insufficient_balance", "رصيد المحفظة لا يسمح بالمبلغ ورسوم التحويل. اشحن المحفظة أو غيّر المبلغ.");
    public static string CanonicalPhone(string value)
    {
        if (value.Length > 30 || value.Any(c => !char.IsDigit(c) && c is not ('+' or '-' or '(' or ')' or ' ')))
            throw new ServiceException("invalid_recipient_phone", "أدخل رقم هاتف المستلم بشكل صحيح.");
        var digits = string.Concat(value.Trim().Where(char.IsDigit).Select(c => char.GetNumericValue(c).ToString("0", CultureInfo.InvariantCulture)));
        if (digits.StartsWith("00967")) digits = digits[5..]; else if (digits.StartsWith("967")) digits = digits[3..];
        if (digits.Length == 10 && digits.StartsWith('0')) digits = digits[1..];
        if (digits.Length != 9) throw new ServiceException("invalid_recipient_phone", "أدخل رقم هاتف يمني صحيحاً من 9 أرقام أو مع رمز الدولة 967.");
        return digits;
    }
    private static object View(TransferEntity t) => new { t.Reference, t.RecipientName, t.RecipientPhone, recipientType = t.RecipientRole == UserRole.Driver ? "سائق" : "عميل",
        t.Amount, t.Fee, total = t.Amount + t.Fee, t.Currency, t.Note, t.ExpiresAtUtc, t.CompletedAtUtc, t.SenderBalanceAfter,
        status = t.CompletedAtUtc != null ? "Completed" : t.ExpiresAtUtc <= DateTime.UtcNow ? "Expired" : "AwaitingConfirmation" };
}
