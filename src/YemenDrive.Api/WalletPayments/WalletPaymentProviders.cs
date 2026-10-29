namespace YemenDrive.Api.WalletPayments;

public static class WalletPaymentProviders
{
    public static bool IsSupported(string? code) => code is "Floosak" or "Jaib";
    public static string Name(string code) => code == "Jaib" ? "جيب" : "فلوسك";
}
