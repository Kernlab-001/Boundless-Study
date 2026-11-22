using BoundlessStudy.Bridge;

namespace BoundlessStudy.Bridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            string? primaryPipe = null;
            string? fallbackPipe = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--pipe" && i + 1 < args.Length)
                {
                    primaryPipe = args[i + 1];
                }
                else if (args[i] == "--fallback-pipe" && i + 1 < args.Length)
                {
                    fallbackPipe = args[i + 1];
                }
            }

            var host = new NativeMessagingHost(
                stdin: stdin,
                stdout: stdout,
                primaryPipeName: primaryPipe,
                fallbackPipeName: fallbackPipe);

            await host.RunAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"BoundlessStudy.Bridge fatal error: {ex.Message}");
            return 1;
        }
    }
}
