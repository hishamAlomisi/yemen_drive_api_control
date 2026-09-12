using System.Diagnostics;

namespace YemenDrive.Api;

public sealed class ConsoleSetupShortcutService(IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine("Press Ctrl+Shift+D to open the database setup page.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.D &&
                        key.Modifiers.HasFlag(ConsoleModifiers.Control) &&
                        key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    {
                        var url = configuration["SetupUrl"] ?? "http://localhost:5080/setup";
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await Task.Delay(100, stoppingToken);
        }
    }
}
