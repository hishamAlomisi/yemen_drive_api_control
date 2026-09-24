using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Areas;

public sealed class ServiceAreaReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config) : ReportsService<ServiceAreaModel>(config)
{
    protected override async Task<object?> ListAsync(ServiceAreaModel model, CancellationToken token) =>
        await db.ServiceAreas.AsNoTracking().OrderBy(x => x.CountryNameAr).ThenBy(x => x.CityNameAr)
            .Select(x => new { x.Id, x.CountryCode, x.CountryNameAr, x.CityNameAr, x.IsActive })
            .ToListAsync(token);

    protected override async Task<object?> ReportAsync(ServiceAreaModel model, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model.CountryCode) || string.IsNullOrWhiteSpace(model.CityNameAr))
            throw new ServiceException("area_required", "الدولة والمدينة مطلوبتان للتحقق.");
        var area = await db.ServiceAreas.AsNoTracking().FirstOrDefaultAsync(x =>
            x.CountryCode == model.CountryCode.Trim().ToUpper() && x.CityNameAr == model.CityNameAr.Trim(), token);
        return new { available = area?.IsActive == true, countryCode = model.CountryCode.Trim().ToUpper(), cityNameAr = model.CityNameAr.Trim() };
    }
}
