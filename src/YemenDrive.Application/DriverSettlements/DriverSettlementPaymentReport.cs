using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Configuration;
using YemenDrive.Database.Entities;
using YemenDrive.Services.Reports;
using YemenDrive.Shared.Api;
using YemenDrive.Shared.Security;

namespace YemenDrive.Application.DriverSettlements;

public sealed class DriverSettlementPaymentReport(
    YemenDriveDbContext db,
    DatabaseConfigurationStore config,
    ICurrentUserContext currentUser) : ReportsService<DriverSettlementPaymentModel>(config)
{
    protected override async Task<object?> ListAsync(DriverSettlementPaymentModel model, CancellationToken token)
    {
        var userId = currentUser.UserId ?? throw new ServiceException("authentication_required", "يجب تسجيل الدخول كمدير.");
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.Role == UserRole.Admin && x.IsActive, token))
            throw new ServiceException("admin_required", "هذه العملية متاحة للإدارة فقط.");
        var rows = await (
            from settlement in db.DriverSettlements.AsNoTracking()
            join driver in db.Users.AsNoTracking() on settlement.DriverId equals driver.Id
            where settlement.NetPayable < 0 && (model.DriverSettlementId == null || settlement.Id == model.DriverSettlementId)
            select new { settlement, driver.DisplayName, driver.PhoneNumber,
                Paid = db.DriverSettlementPayments.Where(x => x.DriverSettlementId == settlement.Id).Sum(x => (decimal?)x.Amount) ?? 0m })
            .OrderByDescending(x => x.settlement.CreatedAtUtc).ToListAsync(token);
        return rows.Select(x => new
        {
            x.settlement.Id, x.settlement.DriverId, driverName = x.DisplayName, driverPhone = x.PhoneNumber,
            x.settlement.GrossRideAmount, x.settlement.PlatformCommission, x.settlement.NetPayable,
            amountDue = -x.settlement.NetPayable, amountPaid = x.Paid,
            amountOutstanding = Math.Max(0m, -x.settlement.NetPayable - x.Paid),
            isSettled = x.Paid >= -x.settlement.NetPayable,
            x.settlement.PaymentReference, x.settlement.CreatedAtUtc
        });
    }
}
