using YemenDrive.Database;
using PaymentMethodEntity = YemenDrive.Database.Entities.PaymentMethod;

namespace YemenDrive.Application.Payments;

public interface IWalletLoginConfiguration
{
    Task ConfigureAsync(YemenDriveDbContext db, PaymentMethodEntity method,
        PaymentMethodModel model, CancellationToken token);
}
