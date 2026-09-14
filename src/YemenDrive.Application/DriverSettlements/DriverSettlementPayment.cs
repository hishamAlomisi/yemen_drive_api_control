using System.Data;
using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using DriverSettlementPaymentEntity = YemenDrive.Database.Entities.DriverSettlementPayment;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.DriverSettlements;

public sealed class DriverSettlementPayment(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : OperationsService<DriverSettlementPaymentModel>(config)
{
    protected override async Task<object?> AddAsync(DriverSettlementPaymentModel model, CancellationToken token)
    {
        var adminId = await RequireAdminAsync(token);
        if (model.DriverSettlementId is null || model.Amount <= 0 || string.IsNullOrWhiteSpace(model.Reference))
            throw new ServiceException("invalid_settlement_payment", "التسوية والمبلغ والمرجع مطلوبة.");

        var reference = model.Reference.Trim();
        if (reference.Length > 128)
            throw new ServiceException("invalid_settlement_reference", "مرجع التسوية أطول من المسموح.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var settlement = await db.DriverSettlements.SingleOrDefaultAsync(x => x.Id == model.DriverSettlementId, token)
            ?? throw new ServiceException("settlement_not_found", "سجل مديونية السائق غير موجود.");
        if (settlement.NetPayable >= 0)
            throw new ServiceException("settlement_not_collectable", "هذا السجل ليس مديونية مستحقة على السائق.");
        var alreadyPaid = await db.DriverSettlementPayments
            .Where(x => x.DriverSettlementId == settlement.Id).SumAsync(x => (decimal?)x.Amount, token) ?? 0m;
        var outstanding = Math.Max(0m, -settlement.NetPayable - alreadyPaid);
        if (outstanding == 0)
            throw new ServiceException("settlement_already_paid", "تمت تسوية هذه المديونية بالكامل مسبقاً.");
        if (model.Amount > outstanding)
            throw new ServiceException("settlement_amount_exceeds_balance", "مبلغ التسوية أكبر من المديونية المتبقية.");
        if (await db.DriverSettlementPayments.AnyAsync(x => x.Reference == reference, token))
            throw new ServiceException("settlement_reference_exists", "مرجع التسوية مستخدم مسبقاً.");

        var walletCurrency = await db.Wallets.Where(x => x.UserId == settlement.DriverId)
            .Select(x => x.Currency).SingleOrDefaultAsync(token) ?? "YER";
        if (!string.Equals(walletCurrency, model.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ServiceException("currency_mismatch", "عملة التسوية لا تطابق عملة حساب السائق.");
        var entity = new DriverSettlementPaymentEntity
        {
            DriverSettlementId = settlement.Id, DriverId = settlement.DriverId, Amount = model.Amount,
            Currency = walletCurrency, Method = string.IsNullOrWhiteSpace(model.Method) ? "CashToPlatform" : model.Method.Trim(),
            Reference = reference, Note = model.Note?.Trim(), SettledByUserId = adminId
        };
        db.DriverSettlementPayments.Add(entity);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new { entity.Id, entity.DriverSettlementId, entity.DriverId, entity.Amount, entity.Currency, entity.Method, entity.Reference, entity.CreatedAtUtc };
    }

    private async Task<int> RequireAdminAsync(CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كمدير.");
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, token))
            throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");
        return userId;
    }
}
