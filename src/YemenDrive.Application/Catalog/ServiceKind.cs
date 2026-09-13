using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using ServiceKindEntity = YemenDrive.Database.Entities.ServiceKind;

namespace YemenDrive.Application.Catalog;

public sealed class ServiceKind(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore) : OperationsService<ServiceKindModel>(configurationStore)
{
    protected override async Task<object?> AddAsync(ServiceKindModel model, CancellationToken cancellationToken)
    {
        ValidateRequired(model);
        if (await dbContext.ServiceKinds.AnyAsync(x => x.Code == model.Code, cancellationToken))
            throw new ServiceException("kind_exists", "رمز نوع الخدمة موجود مسبقاً.");
        var entity = new ServiceKindEntity();
        if (model.IsDefault && model.IsActive) await ClearDefaultAsync(null, cancellationToken);
        Apply(entity, model);
        dbContext.ServiceKinds.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(ServiceKindModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        ValidateRequired(model);
        if (model.IsDefault && model.IsActive) await ClearDefaultAsync(entity.Id, cancellationToken);
        Apply(entity, model);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> DeleteAsync(ServiceKindModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        dbContext.ServiceKinds.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new { entity.Id };
    }

    protected override async Task<object?> GetAsync(ServiceKindModel model, CancellationToken cancellationToken) =>
        ToResult(await FindAsync(model.Id, cancellationToken, true));

    private async Task<ServiceKindEntity> FindAsync(int? id, CancellationToken token, bool noTracking = false)
    {
        if (id is null) throw new ServiceException("id_required", "معرف نوع الخدمة مطلوب.");
        IQueryable<ServiceKindEntity> query = dbContext.ServiceKinds;
        if (noTracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException("kind_not_found", "نوع الخدمة غير موجود.");
    }

    private static void ValidateRequired(ServiceKindModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.NameAr))
            throw new ServiceException("invalid_kind", "رمز نوع الخدمة واسمه مطلوبان.");
    }

    private static void Apply(ServiceKindEntity x, ServiceKindModel model)
    {
        x.Code = model.Code!.Trim();
        x.NameAr = model.NameAr!.Trim();
        x.ImageUrl = model.ImageUrl;
        x.IsActive = model.IsActive;
        x.IsDefault = model.IsDefault && model.IsActive;
        x.SortOrder = model.SortOrder;
    }

    private static object ToResult(ServiceKindEntity x) => new
    {
        x.Id, x.Code, x.NameAr, x.ImageUrl, x.IsActive, x.IsDefault,
        x.SortOrder, x.CreatedAtUtc, x.UpdatedAtUtc
    };

    private async Task ClearDefaultAsync(int? exceptId, CancellationToken cancellationToken)
    {
        var defaults = await dbContext.ServiceKinds
            .Where(x => x.IsDefault && x.IsActive && x.Id != exceptId)
            .ToListAsync(cancellationToken);
        foreach (var item in defaults) item.IsDefault = false;
    }
}
