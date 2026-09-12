using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class NodeNetPipeInteropHarness
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("HARNESS_UNSUPPORTED_PLATFORM");
            return 2;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var mutex = "Vorotex.K15.Runtime.Tests.NodeInterop." + suffix;
        var pipe = "Vorotex.K15.Runtime.Tests.NodeInteropPipe." + suffix;
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex });
        if (!host.TryStart())
        {
            Console.Error.WriteLine("HARNESS_RUNTIME_UNAVAILABLE");
            return 2;
        }

        await using var ingress = new NativeAuthorityIngressServer(host, pipe);
        ingress.Start();
        Console.WriteLine($"READY {pipe}");
        Console.Out.Flush();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        var producerReported = false;
        var decodedReported = 0L;
        var queueReported = false;
        var runningReported = false;
        while (DateTime.UtcNow < deadline)
        {
            var ingressHealth = ingress.Health;
            if (!producerReported && ingressHealth.AcceptedProducers > 0)
            {
                Console.WriteLine("PRODUCER_ACCEPTED");
                Console.Out.Flush();
                producerReported = true;
            }
            while (decodedReported < ingressHealth.DecodedFrames && decodedReported < 2)
            {
                decodedReported++;
                Console.WriteLine($"FRAME_DECODED_{decodedReported}");
                Console.Out.Flush();
            }
            if (!queueReported && ingressHealth.QueueAccepted > 0)
            {
                Console.WriteLine("QUEUE_ACCEPTED");
                Console.Out.Flush();
                queueReported = true;
            }
            var snapshot = host.Snapshot;
            if (!runningReported && snapshot.State == RuntimeState.Running)
            {
                Console.WriteLine("RUNNING");
                Console.Out.Flush();
                runningReported = true;
            }

            if (runningReported && snapshot.Threads.Any(thread =>
                    thread.ThreadId == "node-interop-thread" && thread.RuntimeStatus == ThreadRuntimeStatus.Idle))
            {
                Console.WriteLine("TERMINAL");
                Console.Out.Flush();
                return 0;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        Console.Error.WriteLine("HARNESS_TIMEOUT");
        return 1;
    }
}
