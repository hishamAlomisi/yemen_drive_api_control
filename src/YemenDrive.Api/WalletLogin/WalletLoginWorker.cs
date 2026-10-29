using YemenDrive.Database.Configuration;

namespace YemenDrive.Api.WalletLogin;

public sealed class WalletLoginWorker(
    WalletLoginRuntime runtime, DatabaseConfigurationStore configuration,
    ILogger<WalletLoginWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var reportedFailure = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            if (configuration.IsConfigured)
            {
                try
                {
                    await runtime.ReconcileAsync(stoppingToken);
                    reportedFailure = false;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    if (!reportedFailure)
                        logger.LogWarning("Wallet login configuration cannot be read; it will be checked again.");
                    reportedFailure = true;
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
