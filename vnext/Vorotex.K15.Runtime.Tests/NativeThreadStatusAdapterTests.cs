using Vorotex.K15.Runtime;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class NativeThreadStatusAdapterTests
{
    private static string Payload(string body) => "{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\"," + body + "}";

    public static Task ParsesBoundedNativeStatus()
    {
        var result = NativeThreadStatusAdapter.Parse(Payload(
            "\"threadId\":\"thread-1\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T08:00:00Z\""));
        TestAssert.True(result.IsValid, "valid native payload was rejected");
        TestAssert.Equal(ThreadRuntimeStatus.Active, result.Event!.Status, "status mapping changed");
        TestAssert.Equal(DateTimeOffset.Parse("2026-09-08T08:00:00Z"), result.Event.ObservedUtc, "timestamp changed");
        TestAssert.False(result.Event.IsServiceOrCanary, "default classification is not user");
        return Task.CompletedTask;
    }

    public static Task PreservesFlagsAndClassifications()
    {
        var result = NativeThreadStatusAdapter.Parse(Payload(
            "\"threadId\":\"canary-1\",\"status\":\"active\",\"activeFlags\":[\"waitingOnUserInput\",\"waitingOnApproval\"],\"timestampUtc\":\"2026-09-08T08:00:00Z\",\"classification\":\"canary\""));
        TestAssert.True(result.IsValid, "classified native payload was rejected");
        TestAssert.Equal(2, result.Event!.ActiveFlags.Length, "plural flags were collapsed");
        TestAssert.Equal(ThreadActiveFlag.WaitingOnApproval, result.Event.ActiveFlags[0], "flag order changed");
        TestAssert.Equal(ThreadClassification.Canary, result.Event.Classification, "canary marking changed");
        return Task.CompletedTask;
    }

    public static Task FailsClosed()
    {
        var unknownStatus = NativeThreadStatusAdapter.Parse(Payload("\"threadId\":\"x\",\"status\":\"future\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T08:00:00Z\""));
        TestAssert.False(unknownStatus.IsValid, "unknown status was accepted");
        TestAssert.True(unknownStatus.Diagnostics.Contains("UNKNOWN_NATIVE_STATUS"), "unknown status was not diagnosed");

        var unknownFlag = NativeThreadStatusAdapter.Parse(Payload("\"threadId\":\"x\",\"status\":\"active\",\"activeFlags\":[\"futureFlag\"],\"timestampUtc\":\"2026-09-08T08:00:00Z\""));
        TestAssert.False(unknownFlag.IsValid, "unknown flag was accepted");
        TestAssert.True(unknownFlag.Diagnostics.Contains("UNKNOWN_NATIVE_ACTIVE_FLAG"), "unknown flag was not diagnosed");

        var malformed = NativeThreadStatusAdapter.Parse("{\"threadId\":\"x\",\"status\":\"active\"}");
        TestAssert.False(malformed.IsValid, "missing required fields were accepted");
        var arbitrary = NativeThreadStatusAdapter.Parse(Payload("\"threadId\":\"x\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T08:00:00Z\",\"prompt\":\"secret\""));
        TestAssert.False(arbitrary.IsValid, "arbitrary payload field was accepted");

        const string sensitiveStatus = "SECRET_PROMPT_TOKEN_9f0a";
        const string sensitiveFlag = "COMMAND_WITH_SECRET_7b2c";
        const string sensitiveProperty = "prompt_with_secret_3d1e";
        var sensitive = NativeThreadStatusAdapter.Parse(
            Payload($"\"threadId\":\"x\",\"status\":\"{sensitiveStatus}\",\"activeFlags\":[\"{sensitiveFlag}\"],\"timestampUtc\":\"2026-09-08T08:00:00Z\",\"{sensitiveProperty}\":\"do-not-leak\""));
        TestAssert.False(sensitive.IsValid, "sensitive-looking input was accepted");
        foreach (var diagnostic in sensitive.Diagnostics)
        {
            TestAssert.False(diagnostic.Contains(sensitiveStatus, StringComparison.Ordinal), "raw unknown status leaked into diagnostics");
            TestAssert.False(diagnostic.Contains(sensitiveFlag, StringComparison.Ordinal), "raw unknown flag leaked into diagnostics");
            TestAssert.False(diagnostic.Contains(sensitiveProperty, StringComparison.Ordinal), "raw property name leaked into diagnostics");
            TestAssert.False(diagnostic.Contains("do-not-leak", StringComparison.Ordinal), "arbitrary input leaked into diagnostics");
        }
        return Task.CompletedTask;
    }

    public static Task FeedsRuntimeLifecycle()
    {
        var engine = new RuntimeStateEngine();
        const string thread = "thread-lifecycle";
        var timestamp = "2026-09-08T08:00:00Z";
        var payloads = new[]
        {
            Payload($"\"threadId\":\"{thread}\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"{timestamp}\""),
            Payload($"\"threadId\":\"{thread}\",\"status\":\"active\",\"activeFlags\":[\"waitingOnApproval\"],\"timestampUtc\":\"{timestamp}\""),
            Payload($"\"threadId\":\"{thread}\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"{timestamp}\""),
            Payload($"\"threadId\":\"{thread}\",\"status\":\"idle\",\"timestampUtc\":\"{timestamp}\"")
        };
        var expectedStates = new[] { RuntimeState.Running, RuntimeState.Waiting, RuntimeState.Running };
        for (var index = 0; index < 3; index++)
        {
            var parsed = NativeThreadStatusAdapter.Parse(payloads[index]);
            TestAssert.True(parsed.IsValid, "lifecycle payload was rejected");
            engine.Apply(NativeThreadStatusAdapter.ToObservation(parsed.Event!));
            TestAssert.Equal(expectedStates[index], engine.Snapshot.State, $"lifecycle step {index + 1} changed state unexpectedly");
        }
        engine.Apply(new ThreadAttentionObservation(thread, ThreadAttentionState.Unread,
            DateTimeOffset.Parse(timestamp)));
        var idle = NativeThreadStatusAdapter.Parse(payloads[3]);
        TestAssert.True(idle.IsValid, "idle lifecycle payload was rejected");
        engine.Apply(NativeThreadStatusAdapter.ToObservation(idle.Event!));
        TestAssert.Equal(RuntimeState.DonePendingAttention, engine.Snapshot.State, "idle + unread did not become DONE_PENDING_ATTENTION");
        return Task.CompletedTask;
    }

    public static Task AcceptsExactNonActiveUnionShapes()
    {
        foreach (var status in new[] { "idle", "notLoaded", "systemError" })
        {
            var result = NativeThreadStatusAdapter.Parse(Payload($"\"threadId\":\"{status}-thread\",\"status\":\"{status}\",\"timestampUtc\":\"2026-09-08T08:00:00Z\""));
            TestAssert.True(result.IsValid, $"exact {status} shape was rejected");
            TestAssert.Equal(0, result.Event!.ActiveFlags.Length, $"{status} did not sanitize flags to empty");
        }

        var enrichedIdle = NativeThreadStatusAdapter.Parse(Payload("\"threadId\":\"idle-thread\",\"status\":\"idle\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T08:00:00Z\""));
        TestAssert.False(enrichedIdle.IsValid, "enriched idle shape was accepted");
        return Task.CompletedTask;
    }

    public static Task BridgeHealthDegradesCanonicalRuntimeSnapshot()
    {
        var transport = new NativeStatusTransport();
        var result = transport.Accept("{\"schemaVersion\":\"k15-codex-authority-health/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"authority_degraded\",\"reason\":\"NATIVE_AUTHORITY_DEGRADED_SINK_FAILURE\"}");
        TestAssert.True(result.Accepted, "bridge health envelope was rejected");
        TestAssert.False(transport.Snapshot.Health.IsHealthy, "bridge sink failure did not degrade canonical health");
        TestAssert.Equal("NATIVE_AUTHORITY_DEGRADED_SINK_FAILURE", transport.Snapshot.Health.Detail, "bridge degradation reason changed");
        return Task.CompletedTask;
    }

    public static async Task TransportPreservesOrderedAuthorityAndHealth()
    {
        var engine = new RuntimeStateEngine();
        var transport = new NativeStatusTransport(engine);
        await using var queue = new NativeStatusDeliveryQueue(transport, capacity: 8);
        var timestamp = "2026-09-08T08:00:00Z";
        for (var index = 0; index < 4; index++)
        {
            var status = index == 3 ? "idle" : "active";
            var flags = index == 1 ? "[\"waitingOnApproval\"]" : "[]";
            var flagsField = status == "active" ? $",\"activeFlags\":{flags}" : string.Empty;
            TestAssert.True(queue.TryEnqueue(Payload($"\"threadId\":\"ordered\",\"status\":\"{status}\"{flagsField},\"timestampUtc\":\"{timestamp}\"")), "valid event was not queued");
        }

        for (var attempt = 0; attempt < 100 && queue.Health.Delivered < 4; attempt++)
            await Task.Delay(5).ConfigureAwait(false);

        TestAssert.Equal(4L, queue.Health.Delivered, "queued authority events were lost");
        TestAssert.Equal(RuntimeState.Unknown, transport.Snapshot.State, "idle without attention should not invent DONE state");
        TestAssert.True(queue.Health.IsHealthy, "healthy queue was marked degraded");
    }

    public static async Task ConcurrentQueueNeverReportsAcceptedLoss()
    {
        var transport = new NativeStatusTransport();
        await using var queue = new NativeStatusDeliveryQueue(transport, capacity: 8);
        var payload = Payload("\"threadId\":\"concurrent\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T08:00:00Z\"");
        Parallel.For(0, 200, _ => queue.TryEnqueue(payload));
        for (var attempt = 0; attempt < 100 && queue.Health.Queued != 0; attempt++)
            await Task.Delay(5).ConfigureAwait(false);

        TestAssert.Equal(queue.Health.Accepted, queue.Health.Delivered, "an admitted authority record was lost");
        TestAssert.True(queue.Health.Overflow > 0, "concurrent pressure did not expose overflow");
        TestAssert.False(transport.Snapshot.Health.IsHealthy, "overflow did not degrade canonical runtime health");
        TestAssert.Equal("NATIVE_AUTHORITY_DEGRADED_OVERFLOW", transport.Snapshot.Health.Detail, "degraded reason is not bounded");
    }
}
