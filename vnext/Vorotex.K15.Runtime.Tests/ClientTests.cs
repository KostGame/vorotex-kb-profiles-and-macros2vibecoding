using System.Collections.Immutable;
using Vorotex.K15.Clients;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class ClientTests
{
    public static async Task SharedClientProjectsOneSnapshotAndFiltersDiagnostics()
    {
        var threads = ImmutableArray.Create(
            ThreadSnapshot.Create("user", ThreadRuntimeStatus.Active, new[] { ThreadActiveFlag.WaitingOnApproval }, state: RuntimeState.Waiting, classification: ThreadClassification.User, workingDirectory: @"G:\Мой диск\AgentLoop Exchange\inbox"),
            ThreadSnapshot.Create("service", ThreadRuntimeStatus.Active, state: RuntimeState.Running, classification: ThreadClassification.Service));
        var runtimeSnapshot = new RuntimeSnapshot(1, RuntimeState.Waiting, threads, RuntimeHealthSnapshot.Healthy());
        var snapshot = RuntimeIpcSnapshot.From(runtimeSnapshot, new RuntimeIpcRuntimeHealth(RuntimeContractMetadata.CurrentRuntimeVersion, true, "READY"));
        var transport = new FakeTransport(new RuntimeClientResult(true, new RuntimeIpcResponse(RuntimeIpcMetadata.ProtocolVersion, true, "snapshot", snapshot), ""));
        var client = new RuntimeClientLoop(new RuntimeIpcClient(transport));
        var user = await client.ReadAsync(); var all = await client.ReadAsync(true);
        TestAssert.Equal(1, user.WaitingCount, "waiting projection did not use Runtime state"); TestAssert.Equal(1, user.Threads.Length, "service thread was not hidden");
        TestAssert.Equal(2, all.Threads.Length, "diagnostic toggle did not include service thread"); TestAssert.Equal(2, transport.Calls, "shared client did not request snapshots"); TestAssert.Equal(@"G:\Мой диск\AgentLoop Exchange\inbox", user.Threads[0].WorkingDirectory, "Unicode working directory was not preserved");
    }

    public static async Task ClientFailuresAreBoundedAndReconnectable()
    {
        var transport = new FakeTransport(RuntimeClientResult.Offline("TIMEOUT"), RuntimeClientResult.Offline("DISCONNECTED")); var client = new RuntimeClientLoop(new RuntimeIpcClient(transport));
        TestAssert.Equal("RUNTIME_OFFLINE", (await client.ReadAsync()).Health, "timeout was not degraded"); TestAssert.Equal("RUNTIME_OFFLINE", (await client.ReadAsync()).Health, "disconnect was not degraded");
        TestAssert.Equal(2, transport.Calls, "client did not retry/reconnect through the transport boundary");
    }

    private sealed class FakeTransport(params RuntimeClientResult[] results) : IRuntimeIpcTransport
    { private int _index; public int Calls => _index; public Task<RuntimeClientResult> SendAsync(RuntimeIpcRequest request, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(results[Math.Min(_index++, results.Length - 1)]); }
}
