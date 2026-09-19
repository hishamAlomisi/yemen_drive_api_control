using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using LedgerAccountEntity = YemenDrive.Database.Entities.LedgerAccount;

namespace YemenDrive.Application.Accounting;

/// <summary>
/// Creates the financial owners and posting accounts required by operational
/// entities. It is deliberately idempotent so it can also repair legacy data.
/// No opening balance or journal entry is created here.
/// </summary>
public sealed class FinancialAccountProvisioningService(YemenDriveDbContext db)
{
    private const string Currency = "YER";

    public async Task ProvisionUserAsync(User user, CancellationToken token)
    {
        if (user.Role is not (UserRole.Customer or UserRole.Driver)) return;
        var wallet = user.Wallet ?? await db.Wallets.SingleOrDefaultAsync(x => x.UserId == user.Id, token)
            ?? throw new InvalidOperationException("لا يمكن تجهيز حساب مالي لمستخدم بلا محفظة.");

        var userParty = await EnsurePartyAsync(FinancialPartyType.User, user.Id,
            $"USR-{user.Id}", $"حساب {RoleLabel(user.Role)}: {DisplayName(user)}", token);
        var walletParty = await EnsurePartyAsync(FinancialPartyType.Wallet, wallet.Id,
            $"WLT-{wallet.Id}", $"محفظة {RoleLabel(user.Role)}: {DisplayName(user)}", token);

        await EnsureSystemAccountsAsync(token);
        await EnsureAccountAsync(
            code: $"2100-WLT-{wallet.Id}",
            name: $"محفظة {RoleLabel(user.Role)}: {DisplayName(user)}",
            type: LedgerAccountType.Liability,
            purpose: LedgerAccountPurpose.CustomerWallet,
            party: walletParty,
            parentCode: "2100",
            token);

        if (user.Role == UserRole.Driver)
        {
            await EnsureAccountAsync(
                code: $"1100-DRV-{user.Id}",
                name: $"حساب السائق: {DisplayName(user)}",
                type: LedgerAccountType.Asset,
                purpose: LedgerAccountPurpose.DriverCurrentAccount,
                party: userParty,
                parentCode: "1100",
                token);
        }
    }

    public async Task ProvisionServiceKindAsync(ServiceKind kind, CancellationToken token)
    {
        var party = await EnsurePartyAsync(FinancialPartyType.ServiceKind, kind.Id,
            $"SK-{kind.Id}", $"نوع خدمة: {kind.NameAr}", token);
        await EnsureSystemAccountsAsync(token);
        await EnsureAccountAsync(
            code: $"4100-SK-{kind.Id}", name: $"رسوم الخدمة: {kind.NameAr}",
            type: LedgerAccountType.Revenue, purpose: LedgerAccountPurpose.ServiceFee,
            party: party, parentCode: "4100", token);
        await EnsureAccountAsync(
            code: $"4200-SK-{kind.Id}", name: $"تحصيل الخدمة: {kind.NameAr}",
            type: LedgerAccountType.Revenue, purpose: LedgerAccountPurpose.ServiceCollection,
            party: party, parentCode: "4200", token);
    }

    public async Task<FinancialProvisioningResult> ProvisionExistingAsync(CancellationToken token)
    {
        var beforeParties = await db.FinancialParties.CountAsync(token);
        var beforeAccounts = await db.LedgerAccounts.CountAsync(token);
        await EnsureSystemAccountsAsync(token);
        var users = await db.Users.Include(x => x.Wallet)
            .Where(x => x.Role == UserRole.Customer || x.Role == UserRole.Driver).ToListAsync(token);
        foreach (var user in users) await ProvisionUserAsync(user, token);
        var serviceKinds = await db.ServiceKinds.ToListAsync(token);
        foreach (var serviceKind in serviceKinds) await ProvisionServiceKindAsync(serviceKind, token);
        await db.SaveChangesAsync(token);
        return new FinancialProvisioningResult(
            users.Count, serviceKinds.Count,
            await db.FinancialParties.CountAsync(token) - beforeParties,
            await db.LedgerAccounts.CountAsync(token) - beforeAccounts);
    }

    public async Task EnsureSystemAccountsAsync(CancellationToken token)
    {
        var definitions = new[]
        {
            ("1000", "الصندوق النقدي للمنصة", LedgerAccountType.Asset, LedgerAccountPurpose.CashCollection, true),
            ("1100", "ذمم السائقين", LedgerAccountType.Asset, LedgerAccountPurpose.DriverCurrentAccount, false),
            ("1200", "حسابات تسوية المحافظ الخارجية", LedgerAccountType.Asset, LedgerAccountPurpose.ExternalWallet, true),
            ("2100", "التزامات محافظ العملاء", LedgerAccountType.Liability, LedgerAccountPurpose.CustomerWallet, false),
            ("4100", "رسوم الخدمة", LedgerAccountType.Revenue, LedgerAccountPurpose.ServiceFee, true),
            ("4200", "تحصيل الخدمة", LedgerAccountType.Revenue, LedgerAccountPurpose.ServiceCollection, true),
            ("5100", "عمولات السائقين", LedgerAccountType.Expense, LedgerAccountPurpose.DriverCommission, true)
        };
        var existing = await db.LedgerAccounts.Where(x => definitions.Select(d => d.Item1).Contains(x.Code))
            .ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, token);
        foreach (var definition in definitions)
        {
            if (existing.TryGetValue(definition.Item1, out var account))
            {
                account.IsActive = true;
                account.IsSystem = true;
                account.IsPosting = definition.Item5;
                continue;
            }
            db.LedgerAccounts.Add(new LedgerAccountEntity
            {
                Code = definition.Item1, Name = definition.Item2, Type = definition.Item3,
                Purpose = definition.Item4, Currency = Currency, IsActive = true,
                IsSystem = true, IsPosting = definition.Item5
            });
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(token);
    }

    private async Task<FinancialParty> EnsurePartyAsync(
        FinancialPartyType type, int entityId, string code, string name, CancellationToken token)
    {
        var party = await db.FinancialParties.SingleOrDefaultAsync(x => x.Type == type && x.EntityId == entityId, token);
        if (party is not null)
        {
            party.Name = name;
            party.IsActive = true;
            return party;
        }
        party = new FinancialParty { Type = type, EntityId = entityId, Code = code, Name = name, IsActive = true };
        db.FinancialParties.Add(party);
        await db.SaveChangesAsync(token);
        return party;
    }

    private async Task EnsureAccountAsync(
        string code, string name, LedgerAccountType type, LedgerAccountPurpose purpose,
        FinancialParty party, string parentCode, CancellationToken token)
    {
        var account = await db.LedgerAccounts.SingleOrDefaultAsync(x =>
            x.FinancialPartyId == party.Id && x.Purpose == purpose && x.Currency == Currency, token);
        var parentId = await db.LedgerAccounts.Where(x => x.Code == parentCode).Select(x => (int?)x.Id).SingleAsync(token);
        if (account is not null)
        {
            account.Name = name;
            account.IsActive = true;
            account.IsPosting = true;
            account.ParentLedgerAccountId = parentId;
            return;
        }
        db.LedgerAccounts.Add(new LedgerAccountEntity
        {
            Code = code, Name = name, Type = type, Purpose = purpose, Currency = Currency,
            IsActive = true, IsSystem = false, IsPosting = true,
            FinancialPartyId = party.Id, ParentLedgerAccountId = parentId
        });
    }

    private static string RoleLabel(UserRole role) => role == UserRole.Driver ? "السائق" : "العميل";
    private static string DisplayName(User user) => string.IsNullOrWhiteSpace(user.DisplayName) ? $"#{user.Id}" : user.DisplayName.Trim();
}

public sealed record FinancialProvisioningResult(int UsersProcessed, int ServiceKindsProcessed, int FinancialPartiesCreated, int LedgerAccountsCreated);
