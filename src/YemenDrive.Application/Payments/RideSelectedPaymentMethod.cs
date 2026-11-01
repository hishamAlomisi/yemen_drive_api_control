using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;

namespace YemenDrive.Application.Payments;

public static class RideSelectedPaymentMethod
{
    public static async Task<Dictionary<string, string>> LoadAsync(YemenDriveDbContext db, CancellationToken token)
    {
        var methods = await db.PaymentMethods.AsNoTracking().Select(x => new { x.Code, x.NameAr }).ToListAsync(token);
        var names = methods.ToDictionary(x => x.Code, x => x.NameAr, StringComparer.OrdinalIgnoreCase);
        names["cash"] = "نقداً";
        names["wallet"] = "محفظة يمن درايف";
        return names;
    }
}
