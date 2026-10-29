using eqsWallets;
using YemenDrive.Api.WalletLogin;

namespace YemenDrive.Api.WalletPayments;

public interface IWalletPaymentGateway
{
    Guid InstanceId { get; }
    bool IsReady(int methodId, Guid version);
    Task<WalletStepResult> RequestAsync(int methodId, Guid version, string providerCode, string requestId, string mobile, decimal amount, CancellationToken token);
    Task<WalletStepResult> ConfirmAsync(int methodId, Guid version, string providerCode, string operationId, bool approved, CancellationToken token);
    Task<WalletStepResult> CompleteAsync(int methodId, Guid version, string providerCode, string operationId, string code, CancellationToken token);
}

public sealed class WalletPaymentGateway(WalletLoginRuntime runtime) : IWalletPaymentGateway
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public bool IsReady(int methodId, Guid version) => runtime.GetStatus(methodId, version) == "Ready";
    public Task<WalletStepResult> RequestAsync(int methodId, Guid version, string providerCode, string requestId, string mobile, decimal amount, CancellationToken token) =>
        runtime.WithPaymentClientAsync(methodId, version, providerCode, client => providerCode switch
        {
            "Floosak" => client.Floosak.RequestWithdrawFromCustomerAsync(requestId, mobile, amount, CurrencyCode.YER, CurrencyCode.YER, token),
            "Jaib" => client.Jaib.RequestWithdrawalAsync(requestId, mobile, amount, CurrencyCode.YER, token),
            _ => throw new WalletSessionUnavailableException()
        }, token);
    public Task<WalletStepResult> ConfirmAsync(int methodId, Guid version, string providerCode, string operationId, bool approved, CancellationToken token) =>
        runtime.WithPaymentClientAsync(methodId, version, providerCode, client => providerCode switch
        {
            "Floosak" => client.Floosak.ConfirmAsync(operationId, approved, token),
            "Jaib" => client.Jaib.ConfirmAsync(operationId, approved, token),
            _ => throw new WalletSessionUnavailableException()
        }, token);
    public Task<WalletStepResult> CompleteAsync(int methodId, Guid version, string providerCode, string operationId, string code, CancellationToken token) =>
        runtime.WithPaymentClientAsync(methodId, version, providerCode, client => providerCode switch
        {
            "Floosak" => client.Floosak.CompleteAsync(operationId, code, token),
            "Jaib" => client.Jaib.CompleteAsync(operationId, code, token),
            _ => throw new WalletSessionUnavailableException()
        }, token);
}

/// <summary>Raised only before a provider call, so the orchestration can distinguish a missing local session from an unknown debit.</summary>
public sealed class WalletSessionUnavailableException : Exception;
