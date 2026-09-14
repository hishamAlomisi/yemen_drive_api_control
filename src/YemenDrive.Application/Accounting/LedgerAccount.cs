using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using LedgerAccountEntity = YemenDrive.Database.Entities.LedgerAccount;

namespace YemenDrive.Application.Accounting;

/// <summary>Administration of the chart of accounts. Posted journal lines never edit accounts retroactively.</summary>
public sealed class LedgerAccount(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<LedgerAccountModel>(config)
{
    protected override async Task<object?> AddAsync(LedgerAccountModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name) || model.Type is null)
            throw new ServiceException("invalid_ledger_account", "رمز الحساب واسمه ونوعه مطلوبة.");
        var code = model.Code.Trim().ToUpperInvariant();
        var currency = NormalizeCurrency(model.Currency);
        if (await db.LedgerAccounts.AnyAsync(x => x.Code == code, token))
            throw new ServiceException("ledger_account_exists", "رمز الحساب موجود مسبقاً.");
        if (model.FinancialPartyId is int partyId && !await db.FinancialParties.AnyAsync(x => x.Id == partyId && x.IsActive, token))
            throw new ServiceException("financial_party_not_found", "الجهة المالية غير موجودة أو متوقفة.");
        if (model.ParentLedgerAccountId is int parentId && !await db.LedgerAccounts.AnyAsync(x => x.Id == parentId, token))
            throw new ServiceException("parent_ledger_account_not_found", "الحساب الأب غير موجود.");

        var entity = new LedgerAccountEntity
        {
            Code = code, Name = model.Name.Trim(), Type = model.Type.Value,
            Purpose = model.Purpose, Currency = currency, IsActive = model.IsActive,
            IsSystem = model.IsSystem, IsPosting = model.IsPosting,
            ParentLedgerAccountId = model.ParentLedgerAccountId,
            FinancialPartyId = model.FinancialPartyId
        };
        db.LedgerAccounts.Add(entity);
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(LedgerAccountModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        if (model.Id is null) throw new ServiceException("id_required", "معرف الحساب مطلوب.");
        var entity = await db.LedgerAccounts.SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("ledger_account_not_found", "الحساب المالي غير موجود.");
        var used = await db.JournalLines.AnyAsync(x => x.LedgerAccountId == entity.Id, token);
        if (used && (model.Code is not null || model.Type is not null || !string.Equals(entity.Currency, model.Currency, StringComparison.OrdinalIgnoreCase)))
            throw new ServiceException("ledger_account_immutable", "لا يمكن تعديل رمز أو نوع أو عملة حساب استُخدم في قيد منشور.");
        if (model.Code is not null) entity.Code = model.Code.Trim().ToUpperInvariant();
        if (model.Name is not null) entity.Name = model.Name.Trim();
        if (model.Type is not null) entity.Type = model.Type.Value;
        entity.Purpose = model.Purpose;
        entity.Currency = NormalizeCurrency(model.Currency);
        entity.IsActive = model.IsActive;
        entity.IsPosting = model.IsPosting;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        return ToResult(entity);
    }

    protected override async Task<object?> GetAsync(LedgerAccountModel model, CancellationToken token)
    {
        await AccountingAccess.RequireAdminAsync(db, currentUser, token);
        if (model.Id is null) throw new ServiceException("id_required", "معرف الحساب مطلوب.");
        var entity = await db.LedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.Id, token)
            ?? throw new ServiceException("ledger_account_not_found", "الحساب المالي غير موجود.");
        return ToResult(entity);
    }

    private static string NormalizeCurrency(string value)
    {
        var currency = string.IsNullOrWhiteSpace(value) ? "YER" : value.Trim().ToUpperInvariant();
        if (currency.Length > 12) throw new ServiceException("invalid_currency", "رمز العملة غير صالح.");
        return currency;
    }

    internal static object ToResult(LedgerAccountEntity x) => new
    {
        x.Id, x.Code, x.Name, x.Type, x.Purpose, x.Currency, x.IsActive,
        x.IsSystem, x.IsPosting, x.ParentLedgerAccountId, x.FinancialPartyId,
        x.CreatedAtUtc, x.UpdatedAtUtc
    };
}
