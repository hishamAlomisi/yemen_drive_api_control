using Microsoft.EntityFrameworkCore;
using YemenDrive.Database;
using YemenDrive.Database.Entities;

namespace YemenDrive.Application.Payments;

public sealed record RidePaidMethod(string PaymentProvider, string PaymentMethodNameAr)
{
    public static async Task<Dictionary<int, RidePaidMethod>> LoadAsync(
        YemenDriveDbContext db, int[] rideIds, CancellationToken token)
    {
        var payments = await (from payment in db.PaymentTransactions.AsNoTracking()
            where payment.RideId.HasValue && rideIds.Contains(payment.RideId.Value) && payment.Status == PaymentStatus.Paid
            select new
            {
                RideId = payment.RideId!.Value,
                payment.Provider,
                NameAr = db.PaymentWalletOperations.Where(operation => operation.PaymentTransactionId == payment.Id && operation.Status == "Succeeded")
                    .Select(operation => operation.PaymentMethod.NameAr).FirstOrDefault()
            }).ToListAsync(token);
        return payments.ToDictionary(x => x.RideId, x => new RidePaidMethod(x.Provider,
            !string.IsNullOrWhiteSpace(x.NameAr) ? x.NameAr : x.Provider switch
            {
                "YemenDriveWallet" => "محفظة يمن درايف",
                "Cash" => "النقد",
                "Floosak" => "فلوسك",
                "Jaib" => "جيب",
                _ => x.Provider
            }));
    }
}
