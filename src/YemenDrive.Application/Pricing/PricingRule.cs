using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;
using YemenDrive.Database.Entities;
using PricingRuleEntity = YemenDrive.Database.Entities.PricingRule;

namespace YemenDrive.Application.Pricing;

public sealed class PricingRule(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    ICurrentUserContext currentUser) : OperationsService<PricingRuleModel>(configurationStore)
{
    protected override async Task<object?> AddAsync(PricingRuleModel model, CancellationToken cancellationToken)
    {
        await RequireAdminAsync(cancellationToken);
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
        await RequireAdminAsync(cancellationToken);
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
        await RequireAdminAsync(cancellationToken);
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
        if (model.RouteChangeFee < 0 || model.RouteChangeFee > 999999999 || model.RouteChangeFee != decimal.Truncate(model.RouteChangeFee))
            throw new ServiceException("invalid_route_change_fee", "رسوم تغيير المسار يجب أن تكون مبلغًا صحيحًا غير سالب.");
        if (model.BaseFare < 0 || model.PerKilometer < 0 || model.PerMinute < 0)
            throw new ServiceException("invalid_fare", "مكونات الأجرة لا يمكن أن تكون سالبة.");
        if (model.WaitingFreeMinutes is < 0 or > 120 || model.WaitingPricePerMinute < 0 || model.WaitingMaxFee < 0 || model.WaitingMaxFee != decimal.Truncate(model.WaitingMaxFee) ||
            (model.WaitingPricePerMinute > 0 && model.WaitingMaxFee <= 0))
            throw new ServiceException("invalid_waiting_policy", "حدد مهلة من 0 إلى 120 دقيقة، ورسومًا غير سالبة، وسقفًا موجبًا عند تفعيل الانتظار المدفوع.");
        if (model.MaxPriceDecreasePercent is < 0 or > 100 || model.MaxPriceIncreasePercent is < 0 or > 1000)
            throw new ServiceException("invalid_negotiation_limits", "نسبة الخفض بين 0 و100% ونسبة الرفع بين 0 و1000%.");
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
        x.RouteChangeFee = model.RouteChangeFee;
        x.WaitingFreeMinutes = model.WaitingFreeMinutes;
        x.WaitingPricePerMinute = model.WaitingPricePerMinute;
        x.WaitingMaxFee = model.WaitingMaxFee;
        x.MaxPriceDecreasePercent = model.MaxPriceDecreasePercent;
        x.MaxPriceIncreasePercent = model.MaxPriceIncreasePercent;
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
        x.RouteChangeFee,
        x.WaitingFreeMinutes, x.WaitingPricePerMinute, x.WaitingMaxFee,
        x.MaxPriceDecreasePercent, x.MaxPriceIncreasePercent,
        x.Id, x.ServiceKindId, x.ServiceCatalogItemId, x.BaseFare,
        x.PerKilometer, x.PerMinute, x.ServiceFee, x.DriverCommissionRate,
        x.DriverCommissionFixed, x.CancellationFee, x.DriverShareRate, x.IsActive,
        x.CreatedAtUtc, x.UpdatedAtUtc
    };

    private async Task RequireAdminAsync(CancellationToken token)
    {
        if (currentUser.UserId is not int id || !await dbContext.Users.AnyAsync(x => x.Id == id && x.IsActive && x.Role == UserRole.Admin, token))
            throw new ServiceException("admin_required", "تعديل قواعد التسعير متاح للإدارة فقط.");
    }

    private async Task EnsureSelectionAsync(PricingRuleModel model, CancellationToken cancellationToken)
    {
        var valid = await dbContext.ServiceCatalogItems.AnyAsync(
            x => x.Id == model.ServiceCatalogItemId && x.ServiceKindId == model.ServiceKindId && x.IsActive,
            cancellationToken);
        if (!valid)
            throw new ServiceException("service_not_found", "الخدمة المختارة غير موجودة أو لا تتبع نوع الخدمة المحدد.");
    }
}
