using Microsoft.AspNetCore.DataProtection;

namespace YemenDrive.Api.WalletLogin;

public sealed class WalletCredentialsProtector(IDataProtectionProvider provider)
{
    private IDataProtector ForProvider(string providerCode) => provider.CreateProtector(
        "YemenDrive.WalletCredentials.v1", providerCode.Trim().ToUpperInvariant());

    public string Protect(string providerCode, string json) => ForProvider(providerCode).Protect(json);
    public string Unprotect(string providerCode, string encrypted) => ForProvider(providerCode).Unprotect(encrypted);
}
