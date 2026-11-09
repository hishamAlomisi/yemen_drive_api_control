using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Application.Accounting;
using YemenDrive.Application.Wallets;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Core;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using TransferService = YemenDrive.Application.Wallets.WalletTransfer;

static class WalletTransferScenarios
{
    public static async Task RunAsync(YemenDriveDbContext db, DatabaseConfigurationStore config, int adminId)
    {
        var sender = new User { PhoneNumber = "967711222001", DisplayName = "Sender", PasswordHash = PasswordHash.Create("Transfer-Secret-123") };
        var receiver = new User { PhoneNumber = "967711222002", DisplayName = "Receiver", Role = UserRole.Customer };
        var driver = new User { PhoneNumber = "967711222003", DisplayName = "Driver", Role = UserRole.Driver };
        db.AddRange(sender, receiver, driver); await db.SaveChangesAsync();
        db.Wallets.AddRange(new() { UserId = sender.Id, Balance = 10000 }, new() { UserId = receiver.Id }, new() { UserId = driver.Id }); await db.SaveChangesAsync();
        var service = new TransferService(db, config, new Actor(sender.Id));
        var fees = new WalletTransferFee(db, config, new Actor(adminId));
        Is(!(await Run(new WalletTransferFee(db, config, new Actor(sender.Id)), "add", new WalletTransferFeeModel())).Success, "customer cannot configure transfer fees");
        Is(!(await Quote("711222002", 100)).Success, "missing fee rule prevents transfer instead of assuming free");
        Ok(await Run(fees, "add", new WalletTransferFeeModel(FromAmount: 0, ToAmount: 1000, FeeValue: 10)));
        Ok(await Run(fees, "add", new WalletTransferFeeModel(FromAmount: 1000, FeeKind: "Percent", FeeValue: 2)));
        Is(!(await Run(fees, "add", new WalletTransferFeeModel(FromAmount: 999, ToAmount: 2000))).Success, "overlapping active fee ranges rejected");
        var existingRuleId = await db.WalletTransferFeeRules.Where(x => x.FromAmount == 0).Select(x => x.Id).SingleAsync();
        Is(!(await Run(fees, "add", new WalletTransferFeeModel(Id: existingRuleId, FromAmount: 0, ToAmount: 1000))).Success, "add cannot bypass overlap by supplying an existing rule id");
        var q = await Quote("+967 711222002", 1000); Ok(q);
        var reference = Data(q).GetProperty("reference").GetGuid();
        Is(Data(q).GetProperty("fee").GetDecimal() == 20 && Data(q).GetProperty("recipientType").GetString() == "عميل", "exact tier boundary uses percentage and returns only recipient summary");
        var before = await db.Wallets.Where(x => x.UserId == sender.Id).Select(x => x.Balance).SingleAsync();
        Is(!(await Confirm(reference, "bad")).Success && await db.Wallets.Where(x => x.UserId == sender.Id).Select(x => x.Balance).SingleAsync() == before, "wrong password changes no money");
        Is(!(await Run(new TransferService(db, config, new Actor(receiver.Id)), "get", new WalletTransferModel(Reference: reference))).Success, "another user cannot read a transfer quote or receipt");
        var completed = await Confirm(reference); Ok(completed); Ok(await Confirm(reference));
        Is(Data(completed).GetProperty("status").GetString() == "Completed", "confirmed transfer returns receipt");
        Is(await db.Wallets.Where(x => x.UserId == sender.Id).Select(x => x.Balance).SingleAsync() == 8980 && await db.Wallets.Where(x => x.UserId == receiver.Id).Select(x => x.Balance).SingleAsync() == 1000, "sender pays amount plus fee, recipient gets exact amount");
        Is(await db.WalletTransactions.CountAsync(x => x.ExternalReference == $"wallet-transfer:{reference:D}") == 2, "duplicate confirmation posts wallet movements exactly once");
        var journal = await db.JournalEntries.Include(x => x.Lines).ThenInclude(x => x.LedgerAccount).SingleAsync(x => x.SourceId == reference.ToString("D"));
        Is(journal.TotalDebit == 1020 && journal.TotalCredit == 1020 && journal.Lines.Single(x => x.LedgerAccount.Code == "4110").Credit == 20, "balanced journal credits separate transfer-fee revenue account");
        q = await Quote("٧١١٢٢٢٠٠٣", 500); Ok(q); reference = Data(q).GetProperty("reference").GetGuid();
        Is(Data(q).GetProperty("fee").GetDecimal() == 10 && Data(q).GetProperty("recipientType").GetString() == "سائق", "Arabic phone and fixed fee support driver recipient");
        Ok(await Confirm(reference)); Is(await db.Wallets.Where(x => x.UserId == driver.Id).Select(x => x.Balance).SingleAsync() == 500, "driver wallet receives customer transfer");
        Is(!(await Quote(sender.PhoneNumber, 100)).Success, "self transfer rejected");
        Is(!(await Quote("711222999", 100)).Success, "unknown recipient rejected");
        Is(!(await Quote(receiver.PhoneNumber, 9000)).Success, "insufficient balance includes fees and does not issue confirmation");
        Is(!(await Quote(receiver.PhoneNumber, 1.001m)).Success, "fractional overprecision rejected");
        var duplicateRequest = Guid.NewGuid();
        var same = new WalletTransferModel(RequestId: duplicateRequest, PhoneNumber: receiver.PhoneNumber, Amount: 50, Note: "test");
        var first = await Run(service, "quote", same); var second = await Run(service, "quote", same); Ok(first); Ok(second);
        Is(Data(first).GetProperty("reference").GetGuid() == Data(second).GetProperty("reference").GetGuid(), "quote retry reuses immutable reference");
        Is(!(await Run(service, "quote", same with { Amount = 60 })).Success, "idempotency key cannot be reused for different amount");
        reference = Data(first).GetProperty("reference").GetGuid();
        var row = await db.WalletTransfers.SingleAsync(x => x.Reference == reference); row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        Is(!(await Confirm(reference)).Success, "expired quote cannot transfer money");
        q = await Quote(receiver.PhoneNumber, 100); Ok(q); reference = Data(q).GetProperty("reference").GetGuid();
        var senderWallet = await db.Wallets.SingleAsync(x => x.UserId == sender.Id); var savedBalance = senderWallet.Balance;
        senderWallet.Balance = 50; await db.SaveChangesAsync();
        Is(!(await Confirm(reference)).Success, "balance is rechecked at final confirmation");
        senderWallet.Balance = savedBalance; await db.SaveChangesAsync();
        for (var i = 0; i < 5; i++) Is(!(await Confirm(reference, "wrong")).Success, "password failure blocks confirmation");
        Is(!(await Confirm(reference)).Success && senderWallet.TransferLockedUntilUtc > DateTime.UtcNow, "password rate limit persists across quotes");
        senderWallet.TransferLockedUntilUtc = null; senderWallet.TransferPasswordFailures = 0; await db.SaveChangesAsync();
        // A broken posting account must roll the complete economic event back.
        await new FinancialAccountProvisioningService(db).ProvisionUserAsync(receiver, default);
        var receiverWalletId = await db.Wallets.Where(x => x.UserId == receiver.Id).Select(x => x.Id).SingleAsync();
        var broken = await db.LedgerAccounts.SingleAsync(x => x.Code == $"2100-WLT-{receiverWalletId}"); broken.Currency = "USD"; await db.SaveChangesAsync();
        var failed = await Confirm(reference); Is(!failed.Success, "posting failure aborts transfer");
        db.ChangeTracker.Clear();
        Is(await db.Wallets.Where(x => x.UserId == sender.Id).Select(x => x.Balance).SingleAsync() == savedBalance && !await db.WalletTransactions.AnyAsync(x => x.ExternalReference == $"wallet-transfer:{reference:D}"), "wallet balances and movements roll back with journal failure");
        broken = await db.LedgerAccounts.SingleAsync(x => x.Code == $"2100-WLT-{receiverWalletId}"); broken.Currency = "YER"; await db.SaveChangesAsync();
        // Independent contexts exercise concurrent retries against SQL Server, not a mocked store.
        var connection = db.Database.GetConnectionString();
        async Task<ApiResult> ConcurrentConfirm() { await using var other = new YemenDriveDbContext(new DbContextOptionsBuilder<YemenDriveDbContext>().UseSqlServer(connection).Options, RealtimeScenarioMode.Settings); return await Run(new TransferService(other, config, new Actor(sender.Id)), "confirm", new WalletTransferModel(Reference: reference, Password: "Transfer-Secret-123")); }
        await Task.WhenAll(ConcurrentConfirm(), ConcurrentConfirm());
        db.ChangeTracker.Clear();
        Ok(await Confirm(reference));
        Is(await db.WalletTransactions.CountAsync(x => x.ExternalReference == $"wallet-transfer:{reference:D}") == 2 && await db.JournalEntries.CountAsync(x => x.SourceId == reference.ToString("D")) == 1, "concurrent confirmation retries debit once and post one journal");
        Is(await db.Wallets.Where(x => x.UserId == sender.Id).Select(x => x.Balance).SingleAsync() == savedBalance - 110, "concurrent retry preserves exact sender balance");
        Console.WriteLine("PASS: wallet transfers, fee ranges, password, atomic rollback and concurrency.");

        Task<ApiResult> Quote(string phone, decimal amount) => Run(service, "quote", new WalletTransferModel(RequestId: Guid.NewGuid(), PhoneNumber: phone, Amount: amount));
        Task<ApiResult> Confirm(Guid id, string password = "Transfer-Secret-123") => Run(service, "confirm", new WalletTransferModel(Reference: id, Password: password));
    }
    static JsonElement Data(ApiResult result) => JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    static Task<ApiResult> Run<T>(IModelService s, string op, T m) => s.ExecuteAsync(op, JsonSerializer.SerializeToElement(m), default);
    static void Ok(ApiResult r) { if (!r.Success) throw new Exception(r.Code + ": " + r.Message); }
    static void Is(bool v, string label) { if (!v) throw new Exception(label); Console.WriteLine("PASS: transfer: " + label); }
    sealed class Actor(int id) : ICurrentUserContext { public int? UserId => id; }
}
