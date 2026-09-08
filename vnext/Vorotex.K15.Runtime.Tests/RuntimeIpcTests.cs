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
        var first = RuntimeIpcProtocol.Handle(Snapshot, () => state);
        var second = RuntimeIpcProtocol.Handle(Snapshot, () => state);
        TestAssert.Equal(first, second, "snapshot serialization is not stable");
        using var json = JsonDocument.Parse(first);
        var value = json.RootElement.GetProperty("snapshot");
        TestAssert.Equal("DONE_PENDING_ATTENTION", value.GetProperty("state").GetString(), "canonical state was recomputed");
        TestAssert.Equal(1, value.GetProperty("attention").GetProperty("unreadCount").GetInt32(), "attention count changed");
        TestAssert.Equal("thread-b", value.GetProperty("focusedThreadId").GetString(), "focus hint was lost");
        TestAssert.Equal("NOT_IMPLEMENTED", value.GetProperty("rgb").GetProperty("status").GetString(), "RGB placeholder changed");
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
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = pipeName + ".mutex" });
        TestAssert.True(host.TryStart(), "runtime host failed to start for IPC");
        await using (var server = new RuntimeIpcServer(host, pipeName))
        {
            server.Start();
            var responses = await Task.WhenAll(ReadFromPipeAsync(pipeName), ReadFromPipeAsync(pipeName))
                .WaitAsync(TimeSpan.FromSeconds(5));
            TestAssert.Equal(responses[0], responses[1], "concurrent clients received different snapshots");
            TestAssert.Equal(RuntimeState.Normal, host.Snapshot.State, "reading IPC changed runtime state");
        }
        await using (var restarted = new RuntimeIpcServer(host, pipeName))
        {
            restarted.Start();
            var response = await ReadFromPipeAsync(pipeName);
            TestAssert.True(response.Contains("\"command\":\"ping\""), "fresh client could not reconnect after restart");
        }
    }

    private static async Task<string> ReadFromPipeAsync(string pipeName)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await writer.WriteLineAsync(Ping);
        return await reader.ReadLineAsync() ?? string.Empty;
    }
}
