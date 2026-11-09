using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Api;
using YemenDrive.Application.Accounting;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

static class WalletLedgerScenarios
{
    public static async Task RunAsync(YemenDriveDbContext source, DatabaseConfigurationStore config, int adminId)
    {
        var notificationCapture = new NotificationSaveCaptureInterceptor();
        await using var db = new YemenDriveDbContext(new DbContextOptionsBuilder<YemenDriveDbContext>()
            .UseSqlServer(source.Database.GetConnectionString()).AddInterceptors(new WalletMovementNotificationInterceptor(), notificationCapture).Options, RealtimeScenarioMode.Settings);
        var customer = new User { PhoneNumber = "967711444001", DisplayName = "Ledger customer" };
        var driver = new User { PhoneNumber = "967711444002", DisplayName = "Ledger driver", Role = UserRole.Driver };
        db.AddRange(customer, driver); await db.SaveChangesAsync();
        db.Wallets.AddRange(new() { UserId = customer.Id }, new() { UserId = driver.Id }); await db.SaveChangesAsync();
        var provisioning = new FinancialAccountProvisioningService(db);
        await provisioning.ProvisionUserAsync(customer, default); await provisioning.ProvisionUserAsync(driver, default); await db.SaveChangesAsync();
        var customerWallet = await db.Wallets.SingleAsync(x => x.UserId == customer.Id);
        var driverWallet = await db.Wallets.SingleAsync(x => x.UserId == driver.Id);
        var cash = await db.LedgerAccounts.SingleAsync(x => x.Code == "1000");
        var customerAccount = await db.LedgerAccounts.SingleAsync(x => x.Code == $"2100-WLT-{customerWallet.Id}");
        var driverAccount = await db.LedgerAccounts.SingleAsync(x => x.Code == $"2100-WLT-{driverWallet.Id}");
        var posting = new AccountingPostingService(db);
        var operations = new FinancialOperation(db, config, new Actor(adminId), posting);
        var receipt = new FinancialOperationModel(OperationType: "cashReceipt", DebitAccountId: cash.Id, CreditAccountId: customerAccount.Id, Amount: 1000, OperationDate: new DateOnly(2026, 11, 2), Note: "إيداع نقدي للعميل");
        var paid = await Run("add", receipt); Ok(paid); var receiptId = Data(paid).GetProperty("journalEntryId").GetInt32();
        Is(customerWallet.Balance == 1000 && await db.WalletTransactions.AnyAsync(x => x.WalletId == customerWallet.Id && x.Amount == 1000), "cash receipt updates wallet balance and history with ledger");
        Is(await db.Notifications.AnyAsync(x => x.UserId == customer.Id && x.Title == "إيداع في المحفظة" && x.DataJson!.Contains("open_wallet")), "customer deposit notification is durable and routes to wallet");
        var committedNotifications = await notificationCapture.TakeCommittedAsync(db, default);
        Is(committedNotifications.Count == 1 && await db.NotificationPushOutbox.CountAsync(x => x.Notification.UserId == customer.Id) == 1, "committed deposit is queued for realtime and push delivery once");
        Ok(await Run("add", receipt with { CreditAccountId = driverAccount.Id, Amount = 2000 }));
        Is(driverWallet.Balance == 2000 && await db.Notifications.AnyAsync(x => x.UserId == driver.Id && x.DataJson!.Contains("open_driver_wallet")), "driver receipt updates wallet and notification too");
        var transfer = await Run("add", receipt with { OperationType = "accountTransfer", DebitAccountId = customerAccount.Id, CreditAccountId = driverAccount.Id, Amount = 300 }); Ok(transfer);
        Is(customerWallet.Balance == 700 && driverWallet.Balance == 2300, "administrative financial entry changes both linked wallets");
        // Editing a spent receipt must check the combined reversal/replacement effect, not reject an intermediate balance.
        Ok(await Run("update", receipt with { JournalEntryId = receiptId, Amount = 1200 }));
        Is(customerWallet.Balance == 900, "receipt edit applies net change atomically after funds were partly spent");
        var disbursement = await Run("add", receipt with { OperationType = "cashDisbursement", DebitAccountId = driverAccount.Id, CreditAccountId = cash.Id, Amount = 100 }); Ok(disbursement);
        Is(driverWallet.Balance == 2200 && await db.Notifications.AnyAsync(x => x.UserId == driver.Id && x.Title == "سحب من المحفظة"), "cash disbursement debits wallet and creates withdrawal notification");
        Ok(await Run("delete", new FinancialOperationModel(JournalEntryId: Data(disbursement).GetProperty("journalEntryId").GetInt32())));
        Is(driverWallet.Balance == 2300, "reversing withdrawal returns wallet funds once");
        var count = await db.JournalEntries.CountAsync();
        var notificationsBeforeFailure = await db.Notifications.CountAsync();
        Is(!(await Run("add", receipt with { OperationType = "cashDisbursement", DebitAccountId = customerAccount.Id, CreditAccountId = cash.Id, Amount = 1000 })).Success, "insufficient wallet funds reject financial posting");
        db.ChangeTracker.Clear();
        Is(await db.JournalEntries.CountAsync() == count && await db.Wallets.Where(x => x.Id == customerWallet.Id).Select(x => x.Balance).SingleAsync() == 900, "failed debit leaves no journal and no wallet effect");
        Is(await db.Notifications.CountAsync() == notificationsBeforeFailure, "failed financial posting cannot notify a deposit or withdrawal");
        // Model a receipt written by the old runtime: ledger committed, wallet never updated.
        var legacy = new JournalEntry { EntryNumber = "LEGACY-" + Guid.NewGuid().ToString("N"), Reference = "legacy-wallet-cash", SourceType = "AdminFinancialOperation", SourceId = Guid.NewGuid().ToString("N"),
            Type = JournalEntryType.AdminCashReceipt, Status = JournalEntryStatus.Posted, IsPosted = true, Description = "إيداع سابق لم يصل للمحفظة", Currency = "YER", TotalDebit = 500, TotalCredit = 500 };
        legacy.Lines.Add(new() { LedgerAccountId = cash.Id, Debit = 500, LineNumber = 1 }); legacy.Lines.Add(new() { LedgerAccountId = customerAccount.Id, Credit = 500, LineNumber = 2 });
        db.JournalEntries.Add(legacy); await db.SaveChangesAsync();
        var repair = new WalletLedgerSynchronization(db); await repair.ReconcileExistingAsync(default);
        Is(await db.Wallets.Where(x => x.Id == customerWallet.Id).Select(x => x.Balance).SingleAsync() == 1400, "legacy unsynchronized receipt is repaired without changing original ledger");
        var notifications = await db.Notifications.CountAsync(x => x.UserId == customer.Id);
        await repair.ReconcileExistingAsync(default);
        Is(await db.Wallets.Where(x => x.Id == customerWallet.Id).Select(x => x.Balance).SingleAsync() == 1400 && await db.Notifications.CountAsync(x => x.UserId == customer.Id) == notifications, "repair and notification are idempotent across startup repeats");
        Is(await db.WalletLedgerApplications.CountAsync(x => x.JournalEntryId == legacy.Id) == 1, "repair retains evidence linking journal to wallet application");
        var invalidLegacy = new JournalEntry { EntryNumber = "LEGACY-BAD-" + Guid.NewGuid().ToString("N"), Reference = "legacy-invalid-withdrawal", SourceType = "AdminFinancialOperation", SourceId = Guid.NewGuid().ToString("N"),
            Type = JournalEntryType.AdminCashDisbursement, Status = JournalEntryStatus.Posted, IsPosted = true, Description = "سحب سابق يحتاج مراجعة", Currency = "YER", TotalDebit = 99999, TotalCredit = 99999 };
        invalidLegacy.Lines.Add(new() { LedgerAccountId = driverAccount.Id, Debit = 99999, LineNumber = 1 }); invalidLegacy.Lines.Add(new() { LedgerAccountId = cash.Id, Credit = 99999, LineNumber = 2 });
        var independentLegacy = new JournalEntry { EntryNumber = "LEGACY-GOOD-" + Guid.NewGuid().ToString("N"), Reference = "legacy-valid-deposit", SourceType = "AdminFinancialOperation", SourceId = Guid.NewGuid().ToString("N"),
            Type = JournalEntryType.AdminCashReceipt, Status = JournalEntryStatus.Posted, IsPosted = true, Description = "إيداع مستقل", Currency = "YER", TotalDebit = 50, TotalCredit = 50 };
        independentLegacy.Lines.Add(new() { LedgerAccountId = cash.Id, Debit = 50, LineNumber = 1 }); independentLegacy.Lines.Add(new() { LedgerAccountId = customerAccount.Id, Credit = 50, LineNumber = 2 });
        db.JournalEntries.AddRange(invalidLegacy, independentLegacy); await db.SaveChangesAsync(); await repair.ReconcileExistingAsync(default);
        Is(repair.ReviewMessages.Count == 1 && await db.Wallets.Where(x => x.Id == customerWallet.Id).Select(x => x.Balance).SingleAsync() == 1450 && !await db.WalletLedgerApplications.AnyAsync(x => x.JournalEntryId == invalidLegacy.Id), "invalid legacy withdrawal is held for review without blocking an unrelated valid deposit");
        var approvalModel = new YemenDrive.Application.Wallets.WalletLedgerReconciliationModel(driverWallet.Id, "اعتماد السحب السابق وتسجيل الفرق مديونية بموافقة المشغل");
        var denied = await new YemenDrive.Application.Wallets.WalletLedgerReconciliation(db, config, new Actor(customer.Id)).ExecuteAsync("resolveDebt", JsonSerializer.SerializeToElement(approvalModel), default);
        Is(!denied.Success, "customer cannot approve a historical wallet overdraft");
        Ok(await new YemenDrive.Application.Wallets.WalletLedgerReconciliation(db, config, new Actor(adminId)).ExecuteAsync("resolveDebt", JsonSerializer.SerializeToElement(approvalModel), default));
        db.ChangeTracker.Clear();
        var debtor = await db.Wallets.SingleAsync(x => x.Id == driverWallet.Id);
        Is(debtor.Balance == 0 && debtor.LedgerDebt == 97699, "explicit approval records historical debit as debt with zero spendable balance");
        Is(await db.WalletLedgerApplications.AnyAsync(x => x.JournalEntryId == invalidLegacy.Id && x.DebtApprovedByUserId == adminId && x.DebtApprovalReason != null), "wallet debt approval keeps actor and reason with journal application");
        // Operational credits are already applied by their service; the save layer must consume debt without a second ledger credit.
        await using (var creditTx = await db.Database.BeginTransactionAsync()) {
            debtor.Balance += 100;
            db.WalletTransactions.Add(new() { WalletId = debtor.Id, Type = WalletTransactionType.Credit, Amount = 100, BalanceAfter = debtor.Balance, Description = "إيداع تشغيلي لسداد المديونية" });
            await posting.PostAsync(new(JournalEntryType.WalletTopUp, "debt-operational-credit", "إيداع لسداد المديونية", "YER", [new(cash.Id, Debit: 100), new(driverAccount.Id, Credit: 100)], SourceType: "ExternalWalletTopUp", SourceId: Guid.NewGuid().ToString("N")), default);
            await creditTx.CommitAsync();
        }
        Is(debtor.Balance == 0 && debtor.LedgerDebt == 97599, "operational wallet credit settles debt before becoming spendable");
        Ok(await Run("add", receipt with { CreditAccountId = driverAccount.Id, Amount = 98000 }));
        Is(debtor.Balance == 401 && debtor.LedgerDebt == 0, "administrative deposit clears remaining debt and exposes only the excess");
        var exact = new User { PhoneNumber = "967711444003" }; db.Users.Add(exact); await db.SaveChangesAsync();
        var exactWallet = new Wallet { UserId = exact.Id }; db.Wallets.Add(exactWallet); await db.SaveChangesAsync();
        await provisioning.ProvisionUserAsync(exact, default); await db.SaveChangesAsync();
        var exactAccount = await db.LedgerAccounts.SingleAsync(x => x.Code == $"2100-WLT-{exactWallet.Id}");
        Ok(await Run("add", receipt with { CreditAccountId = exactAccount.Id, Amount = 2451 }));
        var exactLegacy = new JournalEntry { EntryNumber = "EXACT-" + Guid.NewGuid().ToString("N"), Reference = "exact-approved-debt", SourceType = "AdminFinancialOperation", SourceId = Guid.NewGuid().ToString("N"), Type = JournalEntryType.AdminCashDisbursement, Status = JournalEntryStatus.Posted, IsPosted = true, Description = "سحب سابق 3000", TotalDebit = 3000, TotalCredit = 3000 };
        exactLegacy.Lines.Add(new() { LedgerAccountId = exactAccount.Id, Debit = 3000, LineNumber = 1 }); exactLegacy.Lines.Add(new() { LedgerAccountId = cash.Id, Credit = 3000, LineNumber = 2 }); db.JournalEntries.Add(exactLegacy); await db.SaveChangesAsync();
        Ok(await new YemenDrive.Application.Wallets.WalletLedgerReconciliation(db, config, new Actor(adminId)).ExecuteAsync("resolveDebt", JsonSerializer.SerializeToElement(new YemenDrive.Application.Wallets.WalletLedgerReconciliationModel(exactWallet.Id, "اعتماد 3000 وتسجيل 549 مديونية")), default));
        Is(exactWallet.Balance == 0 && exactWallet.LedgerDebt == 549, "2451 minus approved historical withdrawal 3000 becomes 549 debt and zero available");
        Console.WriteLine("PASS: wallet-ledger receipts, disbursements, edits, reversals, notifications and legacy reconciliation.");
        Task<ApiResult> Run(string operation, FinancialOperationModel model) => operations.ExecuteAsync(operation, JsonSerializer.SerializeToElement(model), default);
    }
    static JsonElement Data(ApiResult r) => JsonSerializer.SerializeToElement(r.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    static void Ok(ApiResult r) { if (!r.Success) throw new Exception(r.Code + ": " + r.Message); }
    static void Is(bool v, string label) { if (!v) throw new Exception(label); Console.WriteLine("PASS: wallet ledger: " + label); }
    sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
}
