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
            await WriteAsync(producer, Status("thread-real", "idle", "2026-09-09T08:00:03Z", "[]"));
            await WaitForAsync(() => host.Snapshot.Threads.Any(thread => thread.RuntimeStatus == ThreadRuntimeStatus.Idle &&
                thread.WorkingDirectory == "C:\\work\\real"));
            using var running = JsonDocument.Parse(host.HandleIpcRequest("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"ping\"}"));
            TestAssert.True(running.RootElement.GetProperty("ok").GetBoolean(), "UI command IPC went offline");
        }

        await WaitForAsync(() => !host.Snapshot.Health.IsHealthy);
        TestAssert.Equal("NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE", host.Snapshot.Health.Detail, "disconnect health was not bounded");
        TestAssert.Equal(ThreadRuntimeStatus.Idle, host.Snapshot.Threads.Single(thread => thread.ThreadId == "thread-real").RuntimeStatus, "disconnect changed the terminal idle evidence");

        await using (var reconnected = await ConnectAsync(pipe))
        {
            await Task.Delay(100);
            TestAssert.False(host.Snapshot.Health.IsHealthy, "reconnect without status falsely synchronized authority");
            await WriteAsync(reconnected, Status("thread-real", "active", "2026-09-09T08:00:04Z", "[]"));
            await WaitForAsync(() => host.Snapshot.Health.IsHealthy && host.Snapshot.State == RuntimeState.Running);
        }

        await VerifyStaleNonterminalResyncContractAsync();

        using var finalSnapshot = JsonDocument.Parse(host.HandleIpcRequest("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"snapshot\"}"));
        var projection = finalSnapshot.RootElement.GetProperty("snapshot");
        TestAssert.Equal("DISCONNECTED", projection.GetProperty("device").GetProperty("connectionState").GetString(), "authority lifecycle changed device ownership");
        TestAssert.False(projection.GetProperty("device").GetProperty("protocolVerified").GetBoolean(), "authority lifecycle changed HID verification");
        TestAssert.False(projection.GetProperty("rgb").GetProperty("enabled").GetBoolean(), "authority lifecycle enabled RGB");
    }

    public static async Task ByteStreamFramingPreservesRecordsAndRecoversAfterOversize()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutex = "Vorotex.K15.Runtime.Tests.Framing." + suffix;
        var pipe = "Vorotex.K15.Runtime.Tests.FramingPipe." + suffix;
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex });
        TestAssert.True(host.TryStart(), "runtime failed to start");
        await using var ingress = new NativeAuthorityIngressServer(host, pipe);
        ingress.Start();
        await using var producer = await ConnectAsync(pipe);

        var first = Status("thread-frame", "active", "2026-09-09T08:01:00Z", "[\"waitingOnApproval\"]");
        var second = Status("thread-frame", "active", "2026-09-09T08:01:01Z", "[]");
        var third = Status("thread-frame", "idle", "2026-09-09T08:01:02Z", "[]");
        await WriteBytesAsync(producer, Encoding.UTF8.GetBytes(first + "\n" + second + "\n"));
        await WriteBytesAsync(producer, Encoding.UTF8.GetBytes(third[..40]));
        await WriteBytesAsync(producer, Encoding.UTF8.GetBytes(third[40..] + "\n"));
        var invalid = Encoding.UTF8.GetBytes(Status("invalid", "active", "2026-09-09T08:01:03Z", "[]"));
        var invalidThreadByte = Array.IndexOf(invalid, (byte)'i');
        invalid[invalidThreadByte] = 0xC3;
        await WriteBytesAsync(producer, new string('x', RuntimeIpcMetadata.MaxFrameBytes + 1).Select(ch => (byte)ch)
            .Concat(new byte[] { (byte)'\n' }).Concat(invalid).Concat(new byte[] { (byte)'\n' })
            .Concat(Encoding.UTF8.GetBytes(Metadata("thread-frame", "C:\\frame") + "\n")).ToArray());

        await Task.Delay(250);
        var framedThread = host.Snapshot.Threads.SingleOrDefault(t => t.ThreadId == "thread-frame");
        TestAssert.True(framedThread is not null, "framing did not deliver a thread record");
        TestAssert.Equal(ThreadRuntimeStatus.Idle, framedThread!.RuntimeStatus, $"JSONL record order was not preserved; state={host.Snapshot.State}, health={host.Snapshot.Health.Detail}");
        TestAssert.Equal("C:\\frame", framedThread.WorkingDirectory, "oversized frame recovery lost the following valid record");
        TestAssert.False(host.Snapshot.Threads.Any(t => t.ThreadId == "invalid"), "invalid UTF-8 frame mutated thread state");
        TestAssert.True(host.Snapshot.Health.IsHealthy, $"valid status did not restore health; detail={host.Snapshot.Health.Detail}");
    }

    public static async Task IngressIsAllowlistedBoundedAndSupportsCompetingProducers()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutex = "Vorotex.K15.Runtime.Tests.Authority." + suffix;
        var pipe = "Vorotex.K15.Runtime.Tests.AuthorityPipe." + suffix;
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex });
        TestAssert.True(host.TryStart(), "runtime failed to start");
        await using var ingress = new NativeAuthorityIngressServer(host, pipe, queueCapacity: 2);
        ingress.Start();
        await using var backgroundProducer = await ConnectAsync(pipe);
        await using (var userProducer = await ConnectAsync(pipe, 500))
        {
            await WriteAsync(userProducer, Status("user-thread", "active", "2026-09-09T08:02:00Z", "[\"waitingOnApproval\"]"));
            await WaitForAsync(() => host.Snapshot.State == RuntimeState.Waiting, "user producer did not deliver waiting status");
            await WriteAsync(userProducer, Status("user-thread", "active", "2026-09-09T08:02:01Z", "[]"));
            await WaitForAsync(() => host.Snapshot.State == RuntimeState.Running, "user producer did not deliver running status");
            await WriteAsync(userProducer, Status("user-thread", "idle", "2026-09-09T08:02:02Z", "[]"));
            await WaitForAsync(() => host.Snapshot.Threads.Single(thread => thread.ThreadId == "user-thread").RuntimeStatus == ThreadRuntimeStatus.Idle,
                "user producer did not deliver terminal idle status");
        }
        await Task.Delay(100);
        TestAssert.False(host.Snapshot.Health.IsHealthy, "competing producer disconnect was not visible to authority health");
        await WriteAsync(backgroundProducer, Status("user-thread", "idle", "2026-09-09T08:02:03Z", "[]"));
        await WaitForAsync(() => host.Snapshot.Health.IsHealthy, "remaining producer did not restore authority health with fresh evidence");
        var terminalState = host.Snapshot.State;
        await WriteAsync(backgroundProducer, "{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"shutdown_runtime\"}");
        await WriteAsync(backgroundProducer, new string('x', RuntimeIpcMetadata.MaxFrameBytes + 1));
        await WriteAsync(backgroundProducer, "{\"schemaVersion\":\"future/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"authority_degraded\",\"reason\":\"NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE\"}");
        await Task.Delay(100);
        TestAssert.Equal(terminalState, host.Snapshot.State, "general/unknown ingress record changed state");
        TestAssert.True(host.Snapshot.Health.Detail.StartsWith("NATIVE_AUTHORITY_DEGRADED_", StringComparison.Ordinal), "invalid ingress health was not bounded");
    }

    public static async Task ProducerDisconnectRequiresPerThreadResync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutex = "Vorotex.K15.Runtime.Tests.AuthorityResync." + suffix;
        var pipe = "Vorotex.K15.Runtime.Tests.AuthorityResyncPipe." + suffix;
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = mutex });
        TestAssert.True(host.TryStart(), "runtime failed to start");
        await using var ingress = new NativeAuthorityIngressServer(host, pipe);
        ingress.Start();
        await using var backgroundProducer = await ConnectAsync(pipe);
        await using (var statusProducer = await ConnectAsync(pipe, 500))
        {
            await WriteAsync(statusProducer, Status("A", "active", "2026-09-10T08:03:00Z", "[]"));
            await WaitForAsync(() => host.Snapshot.State == RuntimeState.Running, "status producer did not deliver A running evidence");
        }

        await WaitForAsync(() => host.Snapshot.Health.Detail == "NATIVE_AUTHORITY_DEGRADED_RESYNC_REQUIRED",
            "disconnecting the status producer did not require bounded authority resync");
        await WriteAsync(backgroundProducer, Status("B", "active", "2026-09-10T08:03:01Z", "[]"));
        await WaitForAsync(() => host.Snapshot.Threads.Any(thread => thread.ThreadId == "B"),
            "remaining producer did not deliver unrelated B evidence");
        TestAssert.Equal("NATIVE_AUTHORITY_DEGRADED_RESYNC_REQUIRED", host.Snapshot.Health.Detail,
            "fresh unrelated B evidence certified stale A");

        await WriteAsync(backgroundProducer, Status("A", "active", "2026-09-10T08:03:02Z", "[]"));
        await WaitForAsync(() => host.Snapshot.Health.IsHealthy && host.Snapshot.State == RuntimeState.Running,
            "fresh A evidence did not complete authority resync");
        await WriteAsync(backgroundProducer, Status("A", "idle", "2026-09-10T08:03:03Z", "[]"));
        await WaitForAsync(() => host.Snapshot.Threads.Single(thread => thread.ThreadId == "A").RuntimeStatus == ThreadRuntimeStatus.Idle,
            "remaining producer stopped delivering subsequent A evidence");
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

    private static async Task VerifyStaleNonterminalResyncContractAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "Vorotex.K15.Runtime.Tests.Resync." + suffix });
        TestAssert.True(host.TryStart(), "resync runtime failed to start");
        var pipe = "Vorotex.K15.Runtime.Tests.ResyncPipe." + suffix;
        await using var ingress = new NativeAuthorityIngressServer(host, pipe);
        await using var ui = new RuntimeIpcServer(host, pipe + ".ui");
        ingress.Start(); ui.Start();

        await using (var first = await ConnectAsync(pipe))
        {
            await WriteAsync(first, Status("A", "active", "2026-09-09T08:05:00Z", "[\"waitingOnApproval\"]"));
            await WaitForAsync(() => host.Snapshot.State == RuntimeState.Waiting);
        }
        await WaitForAsync(() => !host.Snapshot.Health.IsHealthy);
        await using (var second = await ConnectAsync(pipe))
        {
            await Task.Delay(50);
            TestAssert.False(host.Snapshot.Health.IsHealthy, "reconnect without status cleared resync degradation");
            await WriteAsync(second, Status("A", "active", "2026-09-09T08:04:59Z", "[\"waitingOnApproval\"]"));
            await WriteAsync(second, Status("A", "active", "2026-09-09T08:05:00Z", "[\"waitingOnApproval\"]"));
            await Task.Delay(50);
            TestAssert.False(host.Snapshot.Health.IsHealthy, "stale/duplicate A evidence cleared resync degradation");
            await WriteAsync(second, Status("B", "active", "2026-09-09T08:05:01Z", "[]"));
            await WaitForAsync(() => host.Snapshot.Threads.Any(t => t.ThreadId == "B"));
            TestAssert.False(host.Snapshot.Health.IsHealthy, "B status certified stale A WAITING");
            await WriteAsync(second, Status("A", "active", "2026-09-09T08:05:02Z", "[]"));
            await WaitForAsync(() => host.Snapshot.Health.IsHealthy && host.Snapshot.State == RuntimeState.Running);
        }

        await using (var third = await ConnectAsync(pipe))
        {
            await WriteAsync(third, Status("A", "active", "2026-09-09T08:05:03Z", "[]"));
            await WaitForAsync(() => host.Snapshot.State == RuntimeState.Running);
        }
        await WaitForAsync(() => !host.Snapshot.Health.IsHealthy);
        await using (var fourth = await ConnectAsync(pipe))
        {
            await WriteAsync(fourth, Status("B", "active", "2026-09-09T08:05:04Z", "[]"));
            await Task.Delay(50);
            TestAssert.False(host.Snapshot.Health.IsHealthy, "B status certified stale A RUNNING");
            await WriteAsync(fourth, Status("A", "idle", "2026-09-09T08:05:05Z", "[]"));
            await WaitForAsync(() => host.Snapshot.Health.IsHealthy);
        }

        using var ping = JsonDocument.Parse(host.HandleIpcRequest("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"ping\"}"));
        TestAssert.True(ping.RootElement.GetProperty("ok").GetBoolean(), "UI IPC went offline during resync");
        TestAssert.False(host.DeviceManager.IsConnected, "resync changed device ownership");
        TestAssert.False(host.RgbController.Enabled, "resync enabled RGB");

        await VerifyQueuedOldGenerationCannotCertifyAsync();
    }

    private static async Task VerifyQueuedOldGenerationCannotCertifyAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "Vorotex.K15.Runtime.Tests.QueueResync." + suffix });
        TestAssert.True(host.TryStart(), "queued resync runtime failed to start");
        var generation = host.BeginNativeAuthorityProducerGeneration();
        host.MarkNativeAuthorityUnavailable(generation);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var completed = new ManualResetEventSlim(false);
        await using var queue = new NativeStatusDeliveryQueue(
            (long recordGeneration, string json) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                var result = host.ApplyNativeAuthorityRecordJson(recordGeneration, json);
                completed.Set();
                return result;
            },
            host.MarkNativeAuthorityDegraded,
            capacity: 4);
        TestAssert.True(queue.TryEnqueue(generation, Status("queued", "active", "2026-09-09T08:07:00Z", "[]")),
            "old-generation record was not queued");
        TestAssert.True(entered.Wait(TimeSpan.FromSeconds(5)), "queue consumer did not reach deterministic barrier");
        var currentGeneration = host.BeginNativeAuthorityProducerGeneration();
        release.Set();
        TestAssert.True(completed.Wait(TimeSpan.FromSeconds(5)), "old queued record did not complete after release");
        TestAssert.Equal(0L, queue.Health.SinkFailures, "expected stale-generation discard became sink failure");
        TestAssert.True(host.Snapshot.Threads.IsEmpty, "old queued generation mutated Runtime state");
        TestAssert.True(host.ApplyNativeAuthorityRecordJson(currentGeneration,
            Status("current", "active", "2026-09-09T08:07:01Z", "[]")).Accepted,
            "current-generation status was rejected");
        TestAssert.True(host.Snapshot.Health.IsHealthy, "current-generation status did not restore authority health");
        using var ping = JsonDocument.Parse(host.HandleIpcRequest("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"ping\"}"));
        TestAssert.True(ping.RootElement.GetProperty("ok").GetBoolean(), "UI IPC went offline during stale discard");
        TestAssert.False(host.DeviceManager.IsConnected, "stale discard changed device ownership");
        TestAssert.False(host.RgbController.Enabled, "stale discard enabled RGB");
    }

    private static string Status(string threadId, string status, string timestamp, string flags)
    {
        var activeFlags = status == "active" ? $",\"activeFlags\":{flags}" : string.Empty;
        return $"{{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\",\"threadId\":\"{threadId}\",\"status\":\"{status}\"{activeFlags},\"timestampUtc\":\"{timestamp}\"}}";
    }

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
        await WriteBytesAsync(pipe, Encoding.UTF8.GetBytes(value + "\n"));
    }

    private static async Task WriteBytesAsync(NamedPipeClientStream pipe, byte[] bytes)
    {
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, string message = "condition did not become true within bounded wait")
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        TestAssert.True(condition(), message);
    }
}
