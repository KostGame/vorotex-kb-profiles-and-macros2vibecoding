using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Vorotex.K15.Runtime;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class NativeAuthorityIngressTests
{
    public static async Task RealPipeLifecycleKeepsRuntimeAndUiOnline()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutex = "Vorotex.K15.Runtime.Tests.Authority." + suffix;
        var pipe = "Vorotex.K15.Runtime.Tests.AuthorityPipe." + suffix;
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex });
        TestAssert.True(host.TryStart(), "canonical runtime failed to start");
        await using var ingress = new NativeAuthorityIngressServer(host, pipe);
        await using var ui = new RuntimeIpcServer(host, pipe + ".ui");
        ingress.Start();
        ui.Start();

        await using (var producer = await ConnectAsync(pipe))
        {
            await WriteAsync(producer, Status("thread-real", "active", "2026-09-09T08:00:00Z", "[]"));
            await WriteAsync(producer, Status("thread-real", "active", "2026-09-09T08:00:01Z", "[\"waitingOnApproval\"]"));
            await WriteAsync(producer, Status("thread-real", "active", "2026-09-09T08:00:02Z", "[]"));
            await WriteAsync(producer, Metadata("thread-real", "C:\\work\\real"));
            await WaitForAsync(() => host.Snapshot.State == RuntimeState.Running &&
                host.Snapshot.Threads.Any(thread => thread.WorkingDirectory == "C:\\work\\real"));
            using var running = JsonDocument.Parse(host.HandleIpcRequest("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"ping\"}"));
            TestAssert.True(running.RootElement.GetProperty("ok").GetBoolean(), "UI command IPC went offline");
        }

        await WaitForAsync(() => !host.Snapshot.Health.IsHealthy);
        TestAssert.Equal("NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE", host.Snapshot.Health.Detail, "disconnect health was not bounded");
        TestAssert.Equal(RuntimeState.Running, host.Snapshot.State, "disconnect manufactured a stale WAITING state");

        await using (var reconnected = await ConnectAsync(pipe))
        {
            await WriteAsync(reconnected, Status("thread-real", "active", "2026-09-09T08:00:03Z", "[]"));
            await WaitForAsync(() => host.Snapshot.Health.IsHealthy && host.Snapshot.State == RuntimeState.Running);
        }

        using var finalSnapshot = JsonDocument.Parse(host.HandleIpcRequest("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"snapshot\"}"));
        var projection = finalSnapshot.RootElement.GetProperty("snapshot");
        TestAssert.Equal("DISCONNECTED", projection.GetProperty("device").GetProperty("connectionState").GetString(), "authority lifecycle changed device ownership");
        TestAssert.False(projection.GetProperty("device").GetProperty("protocolVerified").GetBoolean(), "authority lifecycle changed HID verification");
        TestAssert.False(projection.GetProperty("rgb").GetProperty("enabled").GetBoolean(), "authority lifecycle enabled RGB");
    }

    public static async Task IngressIsAllowlistedBoundedAndSingleProducer()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutex = "Vorotex.K15.Runtime.Tests.Authority." + suffix;
        var pipe = "Vorotex.K15.Runtime.Tests.AuthorityPipe." + suffix;
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex });
        TestAssert.True(host.TryStart(), "runtime failed to start");
        await using var ingress = new NativeAuthorityIngressServer(host, pipe, queueCapacity: 2);
        ingress.Start();
        await using var producer = await ConnectAsync(pipe);
        await TestAssert.ThrowsAsync(() => ConnectAsync(pipe, 250), "second wrapper acquired producer ownership");
        await WriteAsync(producer, "{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"shutdown_runtime\"}");
        await WriteAsync(producer, new string('x', RuntimeIpcMetadata.MaxFrameBytes + 1));
        await WriteAsync(producer, "{\"schemaVersion\":\"future/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"authority_degraded\",\"reason\":\"NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE\"}");
        await Task.Delay(100);
        TestAssert.Equal(RuntimeState.Normal, host.Snapshot.State, "general/unknown ingress record changed state");
        TestAssert.True(host.Snapshot.Health.Detail.StartsWith("NATIVE_AUTHORITY_DEGRADED_", StringComparison.Ordinal), "invalid ingress health was not bounded");
    }

    public static Task BridgeCannotAcquireSecondRuntimeLease()
    {
        var mutex = "Vorotex.K15.Runtime.Tests.SecondRuntime." + Guid.NewGuid().ToString("N");
        using var first = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex });
        using var second = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex });
        TestAssert.True(first.TryStart(), "first runtime did not own lease");
        TestAssert.False(second.TryStart(), "second Runtime acquired canonical lease");
        return Task.CompletedTask;
    }

    private static string Status(string threadId, string status, string timestamp, string flags) =>
        $"{{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\",\"threadId\":\"{threadId}\",\"status\":\"{status}\",\"activeFlags\":{flags},\"timestampUtc\":\"{timestamp}\"}}";

    private static string Metadata(string threadId, string cwd) =>
        $"{{\"schemaVersion\":\"k15-codex-thread-metadata/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_metadata_changed\",\"threadId\":\"{threadId}\",\"workingDirectory\":\"{cwd.Replace("\\", "\\\\", StringComparison.Ordinal)}\"}}";

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, int timeoutMs = 2000)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        try { await client.ConnectAsync(timeoutMs); return client; }
        catch { client.Dispose(); throw; }
    }

    private static async Task WriteAsync(NamedPipeClientStream pipe, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        TestAssert.True(condition(), "condition did not become true within bounded wait");
    }
}
