using System.Collections.Immutable;
using System.IO.Pipes;
using System.Text.Json;
using System.Text;
using Vorotex.K15.Runtime;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class RuntimeIpcTests
{
    private const string Ping = "{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"ping\"}";
    private const string Snapshot = "{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"snapshot\"}";

    public static Task PingWireShapeIsExact()
    {
        TestAssert.Equal("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"ok\":true,\"command\":\"ping\"}",
            RuntimeIpcProtocol.Handle(Ping, () => RuntimeSnapshot.CreateInitial()), "ping wire shape changed");
        return Task.CompletedTask;
    }

    public static Task SnapshotIsCanonicalAndStable()
    {
        var state = new RuntimeSnapshot(1, RuntimeState.DonePendingAttention,
            ImmutableArray.Create(ThreadSnapshot.Create("thread-b", ThreadRuntimeStatus.Idle,
                attention: ThreadAttentionState.Unread, focusHint: ThreadFocusHint.Focused)),
            RuntimeHealthSnapshot.Degraded("test-runtime", "NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE"));
        var first = RuntimeIpcProtocol.Handle(Snapshot, () => state,
            () => new RuntimeIpcRuntimeHealth("test-runtime", true, "READY"));
        var second = RuntimeIpcProtocol.Handle(Snapshot, () => state,
            () => new RuntimeIpcRuntimeHealth("test-runtime", true, "READY"));
        TestAssert.Equal(first, second, "snapshot serialization is not stable");
        using var json = JsonDocument.Parse(first);
        var value = json.RootElement.GetProperty("snapshot");
        TestAssert.Equal("DONE_PENDING_ATTENTION", value.GetProperty("state").GetString(), "canonical state was recomputed");
        TestAssert.Equal(1, value.GetProperty("attention").GetProperty("unreadCount").GetInt32(), "attention count changed");
        TestAssert.Equal("thread-b", value.GetProperty("focusedThreadId").GetString(), "focus hint was lost");
        TestAssert.Equal("NOT_IMPLEMENTED", value.GetProperty("rgb").GetProperty("status").GetString(), "RGB placeholder changed");
        TestAssert.True(value.GetProperty("runtime").GetProperty("isHealthy").GetBoolean(), "runtime health was not sourced");
        TestAssert.True(value.GetProperty("nativeAuthority").GetProperty("isHealthy").GetBoolean() == false, "native health was not kept separate");
        return Task.CompletedTask;
    }

    public static Task OversizedSnapshotReturnsBoundedError()
    {
        var threads = Enumerable.Range(0, 1000)
            .Select(index => ThreadSnapshot.Create("thread-" + index.ToString("D4"), ThreadRuntimeStatus.Idle,
                attention: ThreadAttentionState.Unread))
            .ToImmutableArray();
        var state = new RuntimeSnapshot(1, RuntimeState.DonePendingAttention, threads,
            RuntimeHealthSnapshot.Healthy("test-runtime"));
        var response = RuntimeIpcProtocol.Handle(Snapshot, () => state,
            () => new RuntimeIpcRuntimeHealth("test-runtime", true, "READY"));
        TestAssert.True(response.Length < 256, "oversized snapshot error was not bounded");
        TestAssert.True(response.Contains("\"error\":\"SNAPSHOT_TOO_LARGE\""), "oversized snapshot did not fail closed");
        TestAssert.False(response.Contains("thread-", StringComparison.Ordinal), "oversized response echoed snapshot content");
        return Task.CompletedTask;
    }

    public static Task RuntimeProcessHealthReflectsHostState()
    {
        using var host = new RuntimeHost(new RuntimeHostOptions
        {
            RuntimeVersion = "health-test",
            SingleInstanceName = "Vorotex.K15.Runtime.Tests.Health." + Guid.NewGuid().ToString("N")
        });
        var stopped = JsonDocument.Parse(host.HandleIpcRequest(Snapshot));
        TestAssert.False(stopped.RootElement.GetProperty("snapshot").GetProperty("runtime").GetProperty("isHealthy").GetBoolean(), "stopped host reported healthy");
        TestAssert.Equal("STOPPED", stopped.RootElement.GetProperty("snapshot").GetProperty("runtime").GetProperty("detail").GetString(), "stopped detail changed");
        stopped.Dispose();
        TestAssert.True(host.TryStart(), "health-test host failed to start");
        using var running = JsonDocument.Parse(host.HandleIpcRequest(Snapshot));
        TestAssert.True(running.RootElement.GetProperty("snapshot").GetProperty("runtime").GetProperty("isHealthy").GetBoolean(), "running host reported unhealthy");
        host.Stop();
        using var stoppedAgain = JsonDocument.Parse(host.HandleIpcRequest(Snapshot));
        TestAssert.False(stoppedAgain.RootElement.GetProperty("snapshot").GetProperty("runtime").GetProperty("isHealthy").GetBoolean(), "stopped host reported healthy after Stop");
        return Task.CompletedTask;
    }

    public static Task InvalidRequestsFailClosedWithoutProviderAccess()
    {
        var calls = 0;
        TestAssert.True(RuntimeIpcProtocol.Handle("{", () => RuntimeSnapshot.CreateInitial()).Contains("\"error\":\"MALFORMED\""), "malformed request was accepted");
        TestAssert.True(RuntimeIpcProtocol.Handle("{\"protocolVersion\":\"future\",\"command\":\"snapshot\"}", () => { calls++; return RuntimeSnapshot.CreateInitial(); }).Contains("\"error\":\"UNKNOWN_VERSION\""), "unknown version was accepted");
        TestAssert.True(RuntimeIpcProtocol.Handle("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"future\"}", () => { calls++; return RuntimeSnapshot.CreateInitial(); }).Contains("\"error\":\"UNKNOWN_COMMAND\""), "unknown command was accepted");
        TestAssert.True(RuntimeIpcProtocol.Handle("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"ping\",\"extra\":1}", () => { calls++; return RuntimeSnapshot.CreateInitial(); }).Contains("\"error\":\"UNSUPPORTED_FIELD\""), "unknown field was accepted");
        TestAssert.True(RuntimeIpcProtocol.Handle("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"ping\"}", () => { calls++; return RuntimeSnapshot.CreateInitial(); }).Contains("\"error\":\"UNSUPPORTED_FIELD\""), "duplicate field was accepted");
        TestAssert.True(RuntimeIpcProtocol.Handle("[]", () => { calls++; return RuntimeSnapshot.CreateInitial(); }).Contains("\"error\":\"MALFORMED\""), "non-object request was accepted");
        TestAssert.Equal(0, calls, "rejected requests accessed runtime state");
        return Task.CompletedTask;
    }

    public static Task OversizedAndReservedCommandsAreBounded()
    {
        var response = RuntimeIpcProtocol.Handle(new string('x', RuntimeIpcMetadata.MaxFrameBytes), () => RuntimeSnapshot.CreateInitial());
        TestAssert.True(response.Length < 256 && response.Contains("\"error\":\"MALFORMED\""), "oversized request was not bounded");
        var reserved = RuntimeIpcProtocol.Handle("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"shutdown_runtime\"}", () => RuntimeSnapshot.CreateInitial());
        TestAssert.True(reserved.Contains("\"error\":\"UNSUPPORTED_COMMAND\""), "shutdown was unexpectedly implemented");
        return Task.CompletedTask;
    }

    public static async Task NamedPipeSupportsConcurrentClientsAndRestart()
    {
        var pipeName = "Vorotex.K15.Runtime.Tests.Ipc." + Guid.NewGuid().ToString("N");
        var host1 = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = pipeName + ".mutex" });
        TestAssert.True(host1.TryStart(), "first runtime host failed to start for IPC");
        await using (var server = new RuntimeIpcServer(host1, pipeName))
        {
            server.Start();
            for (var index = 0; index < 1000; index++)
            {
                var nativeStatus = $"{{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\",\"threadId\":\"thread-{index:D4}\",\"status\":\"idle\",\"timestampUtc\":\"2026-09-08T00:00:{index % 60:D2}.{index / 60:D3}Z\"}}";
                TestAssert.True(host1.ApplyNativeStatusJson(nativeStatus).Accepted, "large snapshot seed was rejected");
            }
            var responses = await Task.WhenAll(ReadFromPipeAsync(pipeName, Ping), ReadFromPipeAsync(pipeName, Ping))
                .WaitAsync(TimeSpan.FromSeconds(5));
            TestAssert.Equal(responses[0], responses[1], "concurrent clients received different snapshots");
            TestAssert.Equal(RuntimeState.Unknown, host1.Snapshot.State, "reading IPC changed runtime state");
            var oversizedSnapshot = await ReadFromPipeAsync(pipeName, Snapshot).WaitAsync(TimeSpan.FromSeconds(5));
            TestAssert.True(Encoding.UTF8.GetByteCount(oversizedSnapshot) + 1 <= RuntimeIpcMetadata.MaxFrameBytes, "written snapshot response exceeded limit");
            TestAssert.True(oversizedSnapshot.Contains("\"error\":\"SNAPSHOT_TOO_LARGE\""), "pipe oversized snapshot was not rejected");
            var oversized = await ReadFromPipeAsync(pipeName, new string('x', RuntimeIpcMetadata.MaxFrameBytes + 1))
                .WaitAsync(TimeSpan.FromSeconds(5));
            TestAssert.True(Encoding.UTF8.GetByteCount(oversized) + 1 <= RuntimeIpcMetadata.MaxFrameBytes, "oversized request response exceeded limit");
            TestAssert.True(oversized.Contains("\"error\":\"MALFORMED\""), "pipe oversized request was not rejected");
        }
        host1.Stop();
        host1.Dispose();
        using var host2 = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = pipeName + ".mutex" });
        TestAssert.True(host2.TryStart(), "fresh runtime host did not reacquire identity");
        await using (var restarted = new RuntimeIpcServer(host2, pipeName))
        {
            restarted.Start();
            var response = await ReadFromPipeAsync(pipeName, Ping).WaitAsync(TimeSpan.FromSeconds(5));
            TestAssert.True(response.Contains("\"command\":\"ping\""), "fresh client could not reconnect after restart");
        }
    }

    private static async Task<string> ReadFromPipeAsync(string pipeName, string request)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await writer.WriteLineAsync(request);
        return await reader.ReadLineAsync() ?? string.Empty;
    }
}
