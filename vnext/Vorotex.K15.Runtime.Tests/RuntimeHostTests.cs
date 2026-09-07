using Vorotex.K15.Runtime;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class RuntimeHostTests
{
    public static Task HostEnforcesSingleInstanceOwnership()
    {
        const string singleInstanceName = "Vorotex.K15.Runtime.Tests.Issue140.SingleInstance";
        using var owner = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = singleInstanceName });
        using var contender = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = singleInstanceName });

        TestAssert.True(owner.TryStart(), "first runtime host failed to start");
        TestAssert.True(owner.IsRunning, "first runtime host did not enter running state");
        TestAssert.Equal(RuntimeState.Normal, owner.Snapshot.State, "startup snapshot state changed");
        TestAssert.True(owner.Snapshot.Threads.IsEmpty, "startup snapshot unexpectedly contains a thread");
        TestAssert.False(contender.TryStart(), "second runtime host acquired the single-instance lease");

        owner.Stop();
        TestAssert.False(owner.IsRunning, "runtime host did not stop");
        TestAssert.True(contender.TryStart(), "single-instance lease was not released on stop");
        contender.Stop();
        return Task.CompletedTask;
    }

    public static async Task HostShutsDownThroughCancellation()
    {
        const string singleInstanceName = "Vorotex.K15.Runtime.Tests.Issue140.Cancellation";
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = singleInstanceName });
        using var cancellation = new CancellationTokenSource();

        TestAssert.True(host.TryStart(), "runtime host failed to start");
        var waitTask = host.WaitForShutdownAsync(cancellation.Token);
        cancellation.Cancel();
        await waitTask.ConfigureAwait(false);

        TestAssert.False(host.IsRunning, "cancellation did not shut down the runtime host");
    }
}
