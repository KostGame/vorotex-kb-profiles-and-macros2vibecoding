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

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            await host.WaitForShutdownAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
