namespace YemenDrive.Application.Payments;

public interface IWalletPaymentAvailability
{
    bool IsReady(int methodId, Guid version);
    bool Supports(string providerCode);
}
