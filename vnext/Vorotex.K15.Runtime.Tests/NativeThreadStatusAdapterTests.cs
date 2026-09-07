using Vorotex.K15.Runtime;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class NativeThreadStatusAdapterTests
{
    public static Task ParsesBoundedNativeStatus()
    {
        var result = NativeThreadStatusAdapter.Parse("{" +
            "\"threadId\":\"thread-1\",\"status\":\"active\"," +
            "\"activeFlags\":[],\"timestamp\":\"2026-09-08T08:00:00Z\"}");
        TestAssert.True(result.IsValid, "valid native payload was rejected");
        TestAssert.Equal(ThreadRuntimeStatus.Active, result.Event!.Status, "status mapping changed");
        TestAssert.Equal(DateTimeOffset.Parse("2026-09-08T08:00:00Z"), result.Event.ObservedUtc, "timestamp changed");
        TestAssert.False(result.Event.IsServiceOrCanary, "default classification is not user");
        return Task.CompletedTask;
    }

    public static Task PreservesFlagsAndClassifications()
    {
        var result = NativeThreadStatusAdapter.Parse("{" +
            "\"threadId\":\"canary-1\",\"status\":\"active\"," +
            "\"activeFlags\":[\"waitingOnUserInput\",\"waitingOnApproval\"]," +
            "\"timestamp\":\"2026-09-08T08:00:00Z\",\"classification\":\"canary\"}");
        TestAssert.True(result.IsValid, "classified native payload was rejected");
        TestAssert.Equal(2, result.Event!.ActiveFlags.Length, "plural flags were collapsed");
        TestAssert.Equal(ThreadActiveFlag.WaitingOnApproval, result.Event.ActiveFlags[0], "flag order changed");
        TestAssert.Equal(ThreadClassification.Canary, result.Event.Classification, "canary marking changed");
        return Task.CompletedTask;
    }

    public static Task FailsClosed()
    {
        var unknownStatus = NativeThreadStatusAdapter.Parse("{\"threadId\":\"x\",\"status\":\"future\",\"activeFlags\":[],\"timestamp\":\"2026-09-08T08:00:00Z\"}");
        TestAssert.False(unknownStatus.IsValid, "unknown status was accepted");
        TestAssert.True(unknownStatus.Diagnostics.Any(d => d.StartsWith("UNKNOWN_NATIVE_STATUS", StringComparison.Ordinal)), "unknown status was not diagnosed");

        var unknownFlag = NativeThreadStatusAdapter.Parse("{\"threadId\":\"x\",\"status\":\"active\",\"activeFlags\":[\"futureFlag\"],\"timestamp\":\"2026-09-08T08:00:00Z\"}");
        TestAssert.False(unknownFlag.IsValid, "unknown flag was accepted");
        TestAssert.True(unknownFlag.Diagnostics.Any(d => d.StartsWith("UNKNOWN_NATIVE_ACTIVE_FLAG", StringComparison.Ordinal)), "unknown flag was not diagnosed");

        var malformed = NativeThreadStatusAdapter.Parse("{\"threadId\":\"x\",\"status\":\"active\"}");
        TestAssert.False(malformed.IsValid, "missing required fields were accepted");
        var arbitrary = NativeThreadStatusAdapter.Parse("{\"threadId\":\"x\",\"status\":\"active\",\"activeFlags\":[],\"timestamp\":\"2026-09-08T08:00:00Z\",\"prompt\":\"secret\"}");
        TestAssert.False(arbitrary.IsValid, "arbitrary payload field was accepted");
        return Task.CompletedTask;
    }

    public static Task FeedsRuntimeLifecycle()
    {
        var engine = new RuntimeStateEngine();
        const string thread = "thread-lifecycle";
        var timestamp = "2026-09-08T08:00:00Z";
        var payloads = new[]
        {
            $"{{\"threadId\":\"{thread}\",\"status\":\"active\",\"activeFlags\":[],\"timestamp\":\"{timestamp}\"}}",
            $"{{\"threadId\":\"{thread}\",\"status\":\"active\",\"activeFlags\":[\"waitingOnApproval\"],\"timestamp\":\"{timestamp}\"}}",
            $"{{\"threadId\":\"{thread}\",\"status\":\"active\",\"activeFlags\":[],\"timestamp\":\"{timestamp}\"}}",
            $"{{\"threadId\":\"{thread}\",\"status\":\"idle\",\"activeFlags\":[],\"timestamp\":\"{timestamp}\"}}",
        };
        foreach (var payload in payloads.Take(3))
        {
            var parsed = NativeThreadStatusAdapter.Parse(payload);
            TestAssert.True(parsed.IsValid, "lifecycle payload was rejected");
            engine.Apply(NativeThreadStatusAdapter.ToObservation(parsed.Event!));
        }
        engine.Apply(new ThreadAttentionObservation(thread, ThreadAttentionState.Unread,
            DateTimeOffset.Parse(timestamp)));
        var idle = NativeThreadStatusAdapter.Parse(payloads[3]);
        TestAssert.True(idle.IsValid, "idle lifecycle payload was rejected");
        engine.Apply(NativeThreadStatusAdapter.ToObservation(idle.Event!));
        TestAssert.Equal(RuntimeState.DonePendingAttention, engine.Snapshot.State, "idle did not clear active state with attention evidence");
        return Task.CompletedTask;
    }
}
