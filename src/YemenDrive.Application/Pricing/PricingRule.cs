using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using PricingRuleEntity = YemenDrive.Database.Entities.PricingRule;

namespace YemenDrive.Application.Pricing;

public sealed class PricingRule(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : OperationsService<PricingRuleModel>(configurationStore)
{
    protected override async Task<object?> AddAsync(PricingRuleModel model, CancellationToken cancellationToken)
    {
        Validate(model);
        await EnsureSelectionAsync(model, cancellationToken);
        var entity = new PricingRuleEntity();
        Apply(entity, model);
        dbContext.PricingRules.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(PricingRuleModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        Validate(model);
        await EnsureSelectionAsync(model, cancellationToken);
        Apply(entity, model);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> DeleteAsync(PricingRuleModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        dbContext.PricingRules.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new { entity.Id };
    }

    protected override async Task<object?> GetAsync(PricingRuleModel model, CancellationToken cancellationToken) =>
        ToResult(await FindAsync(model.Id, cancellationToken, true));

    private async Task<PricingRuleEntity> FindAsync(int? id, CancellationToken token, bool noTracking = false)
    {
        if (id is null) throw new ServiceException("id_required", "معرف قاعدة التسعير مطلوب.");
        IQueryable<PricingRuleEntity> query = dbContext.PricingRules;
        if (noTracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException("pricing_rule_not_found", "قاعدة التسعير غير موجودة.");
    }

    private static void Validate(PricingRuleModel model)
    {
        if (model.ServiceKindId is null || model.ServiceCatalogItemId is null)
            throw new ServiceException("invalid_pricing_rule", "نوع الخدمة والخدمة المختارة مطلوبان.");
        if (model.DriverShareRate is < 0 or > 1)
            throw new ServiceException("invalid_share_rate", "نسبة السائق يجب أن تكون بين 0 و1.");
        if (model.ServiceFee < 0)
            throw new ServiceException("invalid_service_fee", "رسوم الخدمة لا يمكن أن تكون سالبة.");
        if (model.DriverCommissionRate is < 0 or > 1)
            throw new ServiceException("invalid_driver_commission_rate", "نسبة عمولة السائق يجب أن تكون بين 0 و1.");
        if (model.DriverCommissionFixed < 0 || model.CancellationFee < 0)
            throw new ServiceException("invalid_financial_fee", "الرسوم الثابتة لا يمكن أن تكون سالبة.");
    }

    private static void Apply(PricingRuleEntity x, PricingRuleModel model)
    {
        x.ServiceKindId = model.ServiceKindId!.Value;
        x.ServiceCatalogItemId = model.ServiceCatalogItemId!.Value;
        x.BaseFare = model.BaseFare;
        x.PerKilometer = model.PerKilometer;
        x.PerMinute = model.PerMinute;
        x.ServiceFee = model.ServiceFee;
        x.DriverCommissionRate = model.DriverCommissionRate;
        x.DriverCommissionFixed = model.DriverCommissionFixed;
        x.CancellationFee = model.CancellationFee;
        x.DriverShareRate = model.DriverShareRate;
        x.IsActive = model.IsActive;
    }

    private static object ToResult(PricingRuleEntity x) => new
    {
        x.Id, x.ServiceKindId, x.ServiceCatalogItemId, x.BaseFare,
        x.PerKilometer, x.PerMinute, x.ServiceFee, x.DriverCommissionRate,
        x.DriverCommissionFixed, x.CancellationFee, x.DriverShareRate, x.IsActive,
        x.CreatedAtUtc, x.UpdatedAtUtc
    };

    private async Task EnsureSelectionAsync(PricingRuleModel model, CancellationToken cancellationToken)
    {
        var valid = await dbContext.ServiceCatalogItems.AnyAsync(
            x => x.Id == model.ServiceCatalogItemId && x.ServiceKindId == model.ServiceKindId && x.IsActive,
            cancellationToken);
        if (!valid)
            throw new ServiceException("service_not_found", "الخدمة المختارة غير موجودة أو لا تتبع نوع الخدمة المحدد.");
    }
}
