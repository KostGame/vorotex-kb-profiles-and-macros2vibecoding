using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

internal static class Program
{
    public static async Task<int> Main()
    {
        using var host = new RuntimeHost();
        if (!host.TryStart())
        {
            Console.Error.WriteLine("Another Vorotex.K15.Runtime instance owns the runtime.");
            return 2;
        }

        Console.WriteLine(RuntimeContractJson.Serialize(host.Snapshot));
        await using var ipc = new RuntimeIpcServer(host);
        ipc.Start();

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (Environment.GetEnvironmentVariable("VOROTEX_K15_RUNTIME_NATIVE_STATUS_STDIN") == "1")
            {
                string? line;
                while ((line = await Console.In.ReadLineAsync(shutdown.Token).ConfigureAwait(false)) is not null)
                    host.ApplyNativeStatusJson(line);
                host.Stop();
            }
            else
            {
                await host.WaitForShutdownAsync(shutdown.Token).ConfigureAwait(false);
            }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
