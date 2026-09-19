using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Services.Reports;

namespace YemenDrive.Application.Payments;

public sealed class PaymentMethodReport(YemenDriveDbContext db, DatabaseConfigurationStore config) : ReportsService<PaymentMethodModel>(config)
{
    protected override async Task<object?> ListAsync(PaymentMethodModel model, CancellationToken token) =>
        (await db.PaymentMethods.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.NameAr).ToListAsync(token)).Select(PaymentMethod.Result).ToList();
}
