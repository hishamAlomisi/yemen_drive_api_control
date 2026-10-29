using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;
using YemenDrive.Shared.Api;

namespace YemenDrive.Application.Payments;

/// <summary>All payment channels take the same ride lock before reserving or posting money.</summary>
public static class RidePaymentGuard
{
    public static async Task LockAsync(YemenDriveDbContext db, int rideId, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A payment ride lock requires a transaction.");
        _ = await db.Rides.FromSqlInterpolated(
                $"SELECT * FROM [Rides] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {rideId}")
            .AsNoTracking().SingleOrDefaultAsync(token)
            ?? throw new ServiceException("ride_not_found", "الرحلة غير موجودة.");
    }

    public static async Task EnsureAvailableAsync(YemenDriveDbContext db, int rideId, CancellationToken token)
    {
        if (await db.PaymentTransactions.AnyAsync(x => x.RideId == rideId && x.Status == PaymentStatus.Paid, token))
            throw new ServiceException("ride_already_paid", "هذه الرحلة مسددة بالفعل ولا يمكن تحصيلها مجدداً.");
        if (await db.PaymentWalletOperations.AnyAsync(x => x.RideId == rideId && x.IsOpen, token))
            throw new ServiceException("wallet_payment_in_progress", "توجد عملية دفع للمحفظة؛ استكملها أو تحقق من نتيجتها قبل استخدام وسيلة أخرى.");
    }
}
