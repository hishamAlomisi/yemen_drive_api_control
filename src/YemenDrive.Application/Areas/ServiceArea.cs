using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Operations;
using YemenDrive.Shared.Api;
using ServiceAreaEntity = YemenDrive.Database.Entities.ServiceArea;

namespace YemenDrive.Application.Areas;

public sealed class ServiceArea(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config) : OperationsService<ServiceAreaModel>(config)
{
    protected override async Task<object?> AddAsync(ServiceAreaModel model, CancellationToken token)
    {
        Validate(model);
        var code = model.CountryCode!.Trim().ToUpperInvariant();
        var city = model.CityNameAr!.Trim();
        var item = await db.ServiceAreas.FirstOrDefaultAsync(x => x.CountryCode == code && x.CityNameAr == city, token);
        if (item is null) db.ServiceAreas.Add(item = new ServiceAreaEntity { CountryCode = code, CountryNameAr = model.CountryNameAr!.Trim(), CityNameAr = city });
        item.CountryNameAr = model.CountryNameAr!.Trim(); item.IsActive = model.IsActive; item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token); return new { item.Id, item.CountryCode, item.CountryNameAr, item.CityNameAr, item.IsActive };
    }
    protected override async Task<object?> UpdateAsync(ServiceAreaModel model, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف المنطقة مطلوب.");
        var item = await db.ServiceAreas.SingleOrDefaultAsync(x => x.Id == model.Id, token) ?? throw new ServiceException("area_not_found", "منطقة الخدمة غير موجودة.");
        if (model.CountryNameAr is not null) item.CountryNameAr = model.CountryNameAr.Trim();
        if (model.IsActive != item.IsActive) item.IsActive = model.IsActive;
        await db.SaveChangesAsync(token); return new { item.Id, item.CountryCode, item.CountryNameAr, item.CityNameAr, item.IsActive };
    }
    protected override async Task<object?> DeleteAsync(ServiceAreaModel model, CancellationToken token)
    {
        if (model.Id is null) throw new ServiceException("id_required", "معرف المنطقة مطلوب.");
        var item = await db.ServiceAreas.SingleOrDefaultAsync(x => x.Id == model.Id, token) ?? throw new ServiceException("area_not_found", "منطقة الخدمة غير موجودة.");
        db.ServiceAreas.Remove(item); await db.SaveChangesAsync(token); return new { item.Id };
    }
    private static void Validate(ServiceAreaModel model)
    {
        if (string.IsNullOrWhiteSpace(model.CountryCode) || string.IsNullOrWhiteSpace(model.CountryNameAr) || string.IsNullOrWhiteSpace(model.CityNameAr))
            throw new ServiceException("area_invalid", "رمز الدولة واسم الدولة والمدينة مطلوبة.");
    }
}
