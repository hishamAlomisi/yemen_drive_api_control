using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Administration;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Catalog;

public sealed class ServiceCatalog(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : OperationsService<ServiceCatalogModel>(configurationStore)
{
    protected override async Task<object?> AddAsync(ServiceCatalogModel model, CancellationToken cancellationToken)
    {
        ValidateRequired(model);
        await EnsureServiceKindExistsAsync(model.ServiceKindId, cancellationToken);
        if (await dbContext.ServiceCatalogItems.AnyAsync(x => x.Code == model.Code, cancellationToken))
            throw new ServiceException("service_exists", "رمز الخدمة موجود مسبقاً.");
        var entity = new RideServiceCatalogItem();
        Apply(entity, model);
        dbContext.ServiceCatalogItems.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(ServiceCatalogModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        ValidateRequired(model);
        await EnsureServiceKindExistsAsync(model.ServiceKindId, cancellationToken);
        if (entity.ServiceKindId != model.ServiceKindId && await HasRelatedRecordsAsync(entity.Id, cancellationToken))
            throw new ServiceException("service_kind_change_blocked", "لا يمكن نقل الخدمة إلى نوع آخر لأنها مرتبطة بسجلات تشغيلية. أنشئ خدمة جديدة أو أوقف الحالية بدلاً من ذلك.");
        if (await dbContext.ServiceCatalogItems.AnyAsync(x => x.Id != entity.Id && x.Code == model.Code!.Trim(), cancellationToken))
            throw new ServiceException("service_exists", "رمز الخدمة موجود مسبقاً.");
        Apply(entity, model);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> DeleteAsync(ServiceCatalogModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        await DeletionGuards.EnsureServiceCatalogItemCanBeDeletedAsync(dbContext, entity.Id, cancellationToken);
        dbContext.ServiceCatalogItems.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new { entity.Id };
    }

    protected override async Task<object?> GetAsync(ServiceCatalogModel model, CancellationToken cancellationToken) =>
        ToResult(await FindAsync(model.Id, cancellationToken, true));

    private async Task<RideServiceCatalogItem> FindAsync(int? id, CancellationToken token, bool noTracking = false)
    {
        if (id is null) throw new ServiceException("id_required", "معرف الخدمة مطلوب.");
        IQueryable<RideServiceCatalogItem> query = dbContext.ServiceCatalogItems;
        if (noTracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException("service_not_found", "الخدمة غير موجودة.");
    }

    private static void ValidateRequired(ServiceCatalogModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.NameAr) ||
            model.ServiceKindId is null ||
            string.IsNullOrWhiteSpace(model.DescriptionAr))
            throw new ServiceException("invalid_service", "الرمز والاسم ونوع الخدمة والوصف مطلوبة.");
    }

    private async Task EnsureServiceKindExistsAsync(int? serviceKindId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.ServiceKinds.AnyAsync(
            x => x.Id == serviceKindId && x.IsActive,
            cancellationToken);
        if (!exists)
            throw new ServiceException("service_kind_not_found", "يجب اختيار نوع خدمة موجود ونشط من لوحة التحكم.");
    }

    private async Task<bool> HasRelatedRecordsAsync(int serviceCatalogItemId, CancellationToken cancellationToken)
    {
        if (await dbContext.DriverProfiles.AnyAsync(x => x.ServiceCatalogItemId == serviceCatalogItemId, cancellationToken))
            return true;
        if (await dbContext.Rides.AnyAsync(x => x.ServiceCatalogItemId == serviceCatalogItemId, cancellationToken))
            return true;
        return await dbContext.PricingRules.AnyAsync(x => x.ServiceCatalogItemId == serviceCatalogItemId, cancellationToken);
    }

    private static void Apply(RideServiceCatalogItem x, ServiceCatalogModel model)
    {
        x.Code = model.Code!.Trim();
        x.NameAr = model.NameAr!.Trim();
        x.ServiceKindId = model.ServiceKindId!.Value;
        x.ArrivalMinutes = model.ArrivalMinutes;
        x.BasePrice = model.BasePrice;
        x.Rating = model.Rating;
        x.Seats = model.Seats;
        x.DescriptionAr = model.DescriptionAr!.Trim();
        x.ImageUrl = string.IsNullOrWhiteSpace(model.ImageUrl) ? null : model.ImageUrl.Trim();
        x.IsRecommended = model.IsRecommended;
        x.IsActive = model.IsActive;
        x.SortOrder = model.SortOrder;
    }

    private static object ToResult(RideServiceCatalogItem x) => new
    {
        x.Id, x.Code, x.NameAr, x.ServiceKindId, x.ArrivalMinutes, x.BasePrice,
        x.Rating, x.Seats, x.DescriptionAr, x.ImageUrl, x.IsRecommended, x.IsActive,
        x.SortOrder, x.CreatedAtUtc, x.UpdatedAtUtc
    };
}
