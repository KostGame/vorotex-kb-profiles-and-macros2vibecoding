using System.Collections.Immutable;
using System.Text.Json;
using Vorotex.K15.Clients;
using Vorotex.K15.Runtime;
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

    public static void NativeAuthorityHealthRemainsSeparate()
    {
        var snapshot = RuntimeIpcSnapshot.From(RuntimeSnapshot.CreateInitial(), new RuntimeIpcRuntimeHealth("runtime", true, "READY")) with
        { NativeAuthority = new RuntimeIpcRuntimeHealth("native", false, "NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE") };
        var projection = RuntimeProjection.From(snapshot);
        TestAssert.True(projection.RuntimeHealth.IsHealthy, "runtime health was not preserved");
        TestAssert.False(projection.NativeAuthorityHealth.IsHealthy, "native authority health was dropped");
        TestAssert.Equal("NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE", projection.NativeAuthorityHealth.Detail, "native detail changed");
        TestAssert.True(projection.Degraded, "combined authority health did not degrade");
    }

    public static async Task ControlCommandsRemainBoundedRuntimeCommands()
    {
        var transport = new RecordingTransport(); var client = new RuntimeIpcClient(transport);
        await client.SendCommandAsync("connect_device", "candidate-a"); await client.SendCommandAsync("set_rgb_enabled", enabled: false); await client.SendCommandAsync("restore_lighting");
        TestAssert.Equal("connect_device", transport.Requests[0].Command, "connect command changed"); TestAssert.Equal("candidate-a", transport.Requests[0].CandidateId, "candidate selection was not explicit");
        TestAssert.Equal(false, transport.Requests[1].Enabled, "RGB off was not explicit"); TestAssert.Equal("restore_lighting", transport.Requests[2].Command, "restore command changed");
    }

    public static async Task RealNamedPipeRuntimeRestartReconnectsWithoutMutation()
    {
        var pipe = "Vorotex.K15.Runtime.Tests.Client." + Guid.NewGuid().ToString("N");
        var mutex = pipe + ".mutex";
        using var first = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex }); TestAssert.True(first.TryStart(), "first runtime did not start");
        var firstServer = new RuntimeIpcServer(first, pipe); firstServer.Start();
        var client = new RuntimeIpcClient(new NamedPipeRuntimeIpcTransport(pipe));
        var status = JsonSerializer.Serialize(new { schemaVersion = "k15-codex-thread-status/v1", source = "codex_stdio_bridge", @event = "thread_status_changed", threadId = "unicode-thread", status = "idle", timestampUtc = "2026-09-08T00:00:00Z" });
        var metadata = JsonSerializer.Serialize(new { schemaVersion = "k15-codex-thread-metadata/v1", source = "codex_stdio_bridge", @event = "thread_metadata_changed", threadId = "unicode-thread", workingDirectory = @"G:\Мой диск\AgentLoop Exchange\inbox" });
        TestAssert.True(first.ApplyNativeStatusJson(status).Accepted, "authoritative status was rejected");
        TestAssert.True(first.ApplyNativeThreadMetadataJson(metadata).Accepted, "thread metadata was rejected");
        TestAssert.Equal(@"G:\Мой диск\AgentLoop Exchange\inbox", RuntimeStateEngine.Import(first.Snapshot).Snapshot.Threads[0].WorkingDirectory, "state engine dropped authoritative metadata");
        var success = await client.SnapshotAsync(); TestAssert.True(success.Success, "named pipe snapshot did not succeed");
        var projection = RuntimeProjection.From(success.Snapshot); TestAssert.Equal(@"G:\Мой диск\AgentLoop Exchange\inbox", projection.Threads[0].WorkingDirectory, "IPC lost Unicode metadata");
        first.NativeStatusTransport.MarkDegraded("NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE");
        var degradedAuthority = RuntimeProjection.From((await client.SnapshotAsync()).Snapshot); TestAssert.False(degradedAuthority.NativeAuthorityHealth.IsHealthy, "IPC lost native authority degradation"); TestAssert.True(degradedAuthority.RuntimeHealth.IsHealthy, "native degradation incorrectly changed runtime process health");
        var before = first.Snapshot; TestAssert.False(first.DeviceManager.IsConnected, "test unexpectedly owns HID"); TestAssert.False(first.RgbController.Enabled, "test unexpectedly enabled RGB");
        first.Stop(); await firstServer.DisposeAsync();
        var degraded = await client.SnapshotAsync(); TestAssert.False(degraded.Success, "stopped runtime remained online"); TestAssert.True(degraded.Error is "DISCONNECTED" or "TIMEOUT", "stop did not degrade through transport");
        using var second = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex }); TestAssert.True(second.TryStart(), "fresh runtime did not reacquire identity"); var secondServer = new RuntimeIpcServer(second, pipe); secondServer.Start();
        try { var reconnected = await client.SnapshotAsync(); TestAssert.True(reconnected.Success, "fresh runtime did not reconnect"); TestAssert.True(second.Snapshot.Threads.IsEmpty, "restart inherited Runtime state"); TestAssert.False(second.DeviceManager.IsConnected, "restart acquired HID"); TestAssert.False(second.RgbController.Enabled, "restart enabled RGB"); TestAssert.Equal(RuntimeState.Normal, second.Snapshot.State, "restart aggregate changed"); TestAssert.Equal(RuntimeState.Unknown, before.State, "fixture baseline changed unexpectedly"); }
        finally { await secondServer.DisposeAsync(); }
    }

    public static async Task ClientFailuresAreBoundedAndReconnectable()
    {
        var transport = new FakeTransport(RuntimeClientResult.Offline("TIMEOUT"), RuntimeClientResult.Offline("DISCONNECTED")); var client = new RuntimeClientLoop(new RuntimeIpcClient(transport));
        TestAssert.Equal("RUNTIME_OFFLINE", (await client.ReadAsync()).Health, "timeout was not degraded"); TestAssert.Equal("RUNTIME_OFFLINE", (await client.ReadAsync()).Health, "disconnect was not degraded");
        TestAssert.Equal(2, transport.Calls, "client did not retry/reconnect through the transport boundary");
    }

    private sealed class FakeTransport(params RuntimeClientResult[] results) : IRuntimeIpcTransport
    { private int _index; public int Calls => _index; public Task<RuntimeClientResult> SendAsync(RuntimeIpcRequest request, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(results[Math.Min(_index++, results.Length - 1)]); }
    private sealed class RecordingTransport : IRuntimeIpcTransport
    { public List<RuntimeIpcRequest> Requests { get; } = new(); public Task<RuntimeClientResult> SendAsync(RuntimeIpcRequest request, TimeSpan timeout, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(new RuntimeClientResult(true, new RuntimeIpcResponse(RuntimeIpcMetadata.ProtocolVersion, true, request.Command), "")); } }
}
