using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Application.Accounting;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Drivers;

public sealed class Driver(
    YemenDriveDbContext dbContext,
    DatabaseConfigurationStore configurationStore,
    FinancialAccountProvisioningService financialAccounts) : OperationsService<DriverModel>(configurationStore)
{
    protected override bool Vaidate(DriverModel model, CancellationToken cancellationToken)
    {
        if (model.UserId is null || model.ServiceKindId is null || model.ServiceCatalogItemId is null ||
            string.IsNullOrWhiteSpace(model.VehicleModel) ||
            string.IsNullOrWhiteSpace(model.PlateNumber))
            throw new ServiceException("invalid_driver", "المستخدم ونوع الخدمة والخدمة والموديل ورقم اللوحة مطلوبة.");

        return true;
    }
    protected override async Task<object?> AddAsync(DriverModel model, CancellationToken cancellationToken)
    {
        

        var user = await dbContext.Users.Include(x => x.DriverProfile)
            .SingleOrDefaultAsync(x => x.Id == model.UserId, cancellationToken)
            ?? throw new ServiceException("user_not_found", "المستخدم غير موجود.");
        if (user.Role == UserRole.Admin)
            throw new ServiceException("admin_cannot_be_driver", "لا يمكن إنشاء ملف سائق لحساب مدير. اختر حساب مستخدم آخر.");
        if (user.DriverProfile is not null)
            throw new ServiceException("driver_exists", "يوجد ملف سائق لهذا المستخدم مسبقاً.");
        await EnsureServiceSelectionAsync(model, cancellationToken);
        var serviceKindId = model.ServiceKindId!.Value;
        var serviceCatalogItemId = model.ServiceCatalogItemId!.Value;
        var vehicleModel = model.VehicleModel!;
        var plateNumber = model.PlateNumber!;

        var entity = new DriverProfile
        {
            UserId = user.Id,
            ServiceKindId = serviceKindId,
            ServiceCatalogItemId = serviceCatalogItemId,
            VehicleModel = vehicleModel.Trim(),
            PlateNumber = plateNumber.Trim(),
            CommissionRate = model.CommissionRate,
            IsAvailable = model.IsAvailable,
            Rating = model.Rating,
            PhotoUrl = model.PhotoUrl
        };
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        user.Role = UserRole.Driver;
        dbContext.DriverProfiles.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await financialAccounts.ProvisionUserAsync(user, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> UpdateAsync(DriverModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        if (model.ServiceKindId is not null || model.ServiceCatalogItemId is not null)
        {
            if (model.ServiceKindId is null || model.ServiceCatalogItemId is null)
                throw new ServiceException("invalid_service_selection", "يجب تحديد نوع الخدمة والخدمة معاً.");
            await EnsureServiceSelectionAsync(model, cancellationToken);
            entity.ServiceKindId = model.ServiceKindId.Value;
            entity.ServiceCatalogItemId = model.ServiceCatalogItemId.Value;
        }
        if (!string.IsNullOrWhiteSpace(model.VehicleModel)) entity.VehicleModel = model.VehicleModel.Trim();
        if (!string.IsNullOrWhiteSpace(model.PlateNumber)) entity.PlateNumber = model.PlateNumber.Trim();
        entity.CommissionRate = model.CommissionRate;
        entity.IsAvailable = model.IsAvailable;
        entity.Rating = model.Rating;
        entity.PhotoUrl = model.PhotoUrl ?? entity.PhotoUrl;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResult(entity);
    }

    protected override async Task<object?> DeleteAsync(DriverModel model, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(model.Id, cancellationToken);
        dbContext.DriverProfiles.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new { entity.Id };
    }

    protected override async Task<object?> GetAsync(DriverModel model, CancellationToken cancellationToken) =>
        ToResult(await FindAsync(model.Id, cancellationToken, true));

    private async Task<DriverProfile> FindAsync(int? id, CancellationToken cancellationToken, bool noTracking = false)
    {
        if (id is null) throw new ServiceException("id_required", "معرف ملف السائق مطلوب.");
        IQueryable<DriverProfile> query = dbContext.DriverProfiles
            .Include(x => x.ServiceKind).Include(x => x.ServiceCatalogItem);
        if (noTracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ServiceException("driver_not_found", "ملف السائق غير موجود.");
    }

    private async Task EnsureServiceSelectionAsync(DriverModel model, CancellationToken cancellationToken)
    {
        var valid = await dbContext.ServiceCatalogItems.AnyAsync(
            x => x.Id == model.ServiceCatalogItemId && x.ServiceKindId == model.ServiceKindId && x.IsActive,
            cancellationToken);
        if (!valid)
            throw new ServiceException("service_not_found", "الخدمة المختارة غير موجودة أو غير متاحة لهذا النوع.");
    }

    private static object ToResult(DriverProfile x) => new
    {
        x.Id, x.UserId, x.ServiceKindId, x.ServiceCatalogItemId,
        serviceKindCode = x.ServiceKind?.Code, serviceKindNameAr = x.ServiceKind?.NameAr,
        serviceCode = x.ServiceCatalogItem?.Code, serviceNameAr = x.ServiceCatalogItem?.NameAr,
        x.VehicleModel,
        x.PlateNumber, x.CommissionRate, x.IsAvailable, x.Rating, x.PhotoUrl,
        x.CreatedAtUtc, x.UpdatedAtUtc
    };
}
