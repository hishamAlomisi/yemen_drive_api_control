using eqsWallets;

namespace YemenDrive.Api.WalletLogin;

public interface IWalletLoginClient : IAsyncDisposable
{
    Task<bool> LoginAsync(WalletOptions options, CancellationToken token);
}

public interface IWalletLoginClientFactory
{
    IWalletLoginClient Create();
}

public sealed class WalletLoginClientFactory : IWalletLoginClientFactory
{
    public IWalletLoginClient Create() => new WalletLoginClient();
}

public sealed class WalletLoginClient : IWalletLoginClient
{
    // An instance isolates provider sessions from all other payment records.
    public WalletClient Client { get; } = new();
    public Task<bool> LoginAsync(WalletOptions options, CancellationToken token) => Client.LoginAsync(options, token);
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
