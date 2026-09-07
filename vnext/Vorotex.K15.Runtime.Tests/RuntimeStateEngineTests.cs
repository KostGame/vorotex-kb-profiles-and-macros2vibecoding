using System.Collections.Immutable;
using Vorotex.K15.Runtime;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class RuntimeStateEngineTests
{
    public static Task MapsNativeStatusAndAttentionEvidence()
    {
        var cases = new[]
        {
            (ThreadRuntimeStatus.Active, Array.Empty<ThreadActiveFlag>(), RuntimeState.Running),
            (ThreadRuntimeStatus.Active, new[] { ThreadActiveFlag.WaitingOnApproval }, RuntimeState.Waiting),
            (ThreadRuntimeStatus.Active, new[] { ThreadActiveFlag.WaitingOnUserInput }, RuntimeState.Waiting),
            (ThreadRuntimeStatus.Active, new[] { ThreadActiveFlag.WaitingOnApproval, ThreadActiveFlag.WaitingOnUserInput }, RuntimeState.Waiting),
            (ThreadRuntimeStatus.SystemError, Array.Empty<ThreadActiveFlag>(), RuntimeState.Blocked),
        };

        foreach (var (status, flags, expected) in cases)
        {
            var engine = new RuntimeStateEngine();
            engine.Apply(new ThreadRuntimeObservation("thread", status, flags));
            TestAssert.Equal(expected, engine.Snapshot.State, $"status mapping failed for {status}");
            TestAssert.Equal(expected, engine.Snapshot.Threads[0].State, $"thread mapping failed for {status}");
        }

        var unread = new RuntimeStateEngine();
        unread.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Idle));
        var unreadResult = unread.Apply(new ThreadAttentionObservation("thread", ThreadAttentionState.Unread));
        TestAssert.Equal(RuntimeState.DonePendingAttention, unreadResult.Snapshot.State, "idle unread did not become DONE_PENDING_ATTENTION");

        var read = new RuntimeStateEngine();
        read.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Idle));
        var readResult = read.Apply(new ThreadAttentionObservation("thread", ThreadAttentionState.Read));
        TestAssert.Equal(RuntimeState.Normal, readResult.Snapshot.State, "idle read did not become NORMAL");

        var notLoaded = new RuntimeStateEngine();
        var notLoadedResult = notLoaded.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.NotLoaded));
        TestAssert.True(notLoadedResult.Diagnostics.Contains("NOT_LOADED_NO_LIVE_RUNTIME_AUTHORITY"), "NOT_LOADED did not produce diagnostics");
        TestAssert.Equal(RuntimeState.Unknown, notLoadedResult.Snapshot.State, "NOT_LOADED manufactured live authority");

        var unknown = new RuntimeStateEngine();
        var unknownResult = unknown.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Unknown));
        TestAssert.True(unknownResult.Diagnostics.Contains("UNKNOWN_NATIVE_RUNTIME_STATUS"), "unknown status did not produce diagnostics");
        TestAssert.Equal(RuntimeState.Unknown, unknownResult.Snapshot.State, "unknown status did not fail closed");

        var unknownFlag = new RuntimeStateEngine();
        var unknownFlagResult = unknownFlag.Apply(new ThreadRuntimeObservation(
            "thread",
            ThreadRuntimeStatus.Active,
            [ThreadActiveFlag.WaitingOnApproval, ThreadActiveFlag.Unknown]));
        TestAssert.True(unknownFlagResult.Diagnostics.Contains("UNKNOWN_NATIVE_ACTIVE_FLAG"), "unknown flag did not produce diagnostics");
        TestAssert.Equal(RuntimeState.Unknown, unknownFlagResult.Snapshot.State, "unknown active flag did not fail closed");
        return Task.CompletedTask;
    }

    public static Task OwnerLiveStickyWaitingRegression()
    {
        var engine = new RuntimeStateEngine();

        engine.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Active));
        TestAssert.Equal(RuntimeState.Running, engine.Snapshot.State, "initial ACTIVE did not become RUNNING");

        engine.Apply(new ThreadRuntimeObservation(
            "thread",
            ThreadRuntimeStatus.Active,
            [ThreadActiveFlag.WaitingOnApproval]));
        TestAssert.Equal(RuntimeState.Waiting, engine.Snapshot.State, "approval wait did not become WAITING");

        engine.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Active));
        TestAssert.Equal(RuntimeState.Running, engine.Snapshot.State, "ACTIVE after wait remained sticky WAITING");

        engine.Apply(new ThreadAttentionObservation("thread", ThreadAttentionState.Unread));
        engine.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Idle));
        TestAssert.Equal(RuntimeState.DonePendingAttention, engine.Snapshot.State, "IDLE did not use independent attention evidence");
        return Task.CompletedTask;
    }

    public static Task AggregatePriorityIsExactAndDeterministic()
    {
        var engine = new RuntimeStateEngine();
        engine.Apply(new ThreadRuntimeObservation("z-running", ThreadRuntimeStatus.Active));
        engine.Apply(new ThreadRuntimeObservation("a-waiting", ThreadRuntimeStatus.Active, [ThreadActiveFlag.WaitingOnUserInput]));
        engine.Apply(new ThreadRuntimeObservation("m-blocked", ThreadRuntimeStatus.SystemError));
        TestAssert.Equal(RuntimeState.Waiting, engine.Snapshot.State, "WAITING did not outrank BLOCKED and RUNNING");
        TestAssert.Equal("a-waiting", engine.Snapshot.Threads[0].ThreadId, "thread snapshots are not ordinally sorted");

        var blocked = new RuntimeStateEngine();
        blocked.Apply(new ThreadRuntimeObservation("running", ThreadRuntimeStatus.Active));
        blocked.Apply(new ThreadRuntimeObservation("blocked", ThreadRuntimeStatus.SystemError));
        TestAssert.Equal(RuntimeState.Blocked, blocked.Snapshot.State, "BLOCKED did not outrank RUNNING");

        var done = new RuntimeStateEngine();
        done.Apply(new ThreadRuntimeObservation("running", ThreadRuntimeStatus.Active));
        done.Apply(new ThreadRuntimeObservation("done", ThreadRuntimeStatus.Idle));
        done.Apply(new ThreadAttentionObservation("done", ThreadAttentionState.Unread));
        TestAssert.Equal(RuntimeState.DonePendingAttention, done.Snapshot.State, "DONE_PENDING_ATTENTION did not outrank RUNNING");
        return Task.CompletedTask;
    }

    public static Task StaleDuplicateAndLegacyInputsFailClosed()
    {
        var engine = new RuntimeStateEngine();
        var fresh = DateTimeOffset.Parse("2026-09-08T09:00:00Z");
        engine.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Active, observedUtc: fresh));
        var beforeDuplicate = RuntimeContractJson.Serialize(engine.Snapshot);

        var duplicate = engine.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Active, observedUtc: fresh));
        TestAssert.True(duplicate.Diagnostics.Contains("DUPLICATE_NATIVE_RUNTIME_OBSERVATION_IGNORED"), "duplicate event was not diagnosed");
        TestAssert.Equal(beforeDuplicate, RuntimeContractJson.Serialize(engine.Snapshot), "duplicate event changed the snapshot");

        var stale = engine.Apply(new ThreadRuntimeObservation("thread", ThreadRuntimeStatus.Idle, observedUtc: fresh.AddSeconds(-1)));
        TestAssert.True(stale.Diagnostics.Contains("STALE_NATIVE_RUNTIME_OBSERVATION_IGNORED"), "stale event was not diagnosed");
        TestAssert.Equal(RuntimeState.Running, stale.Snapshot.State, "stale event overrode fresher native authority");

        var legacy = engine.Apply(new ThreadRuntimeObservation(
            "thread",
            ThreadRuntimeStatus.Active,
            [ThreadActiveFlag.WaitingOnApproval],
            fresh.AddSeconds(10),
            RuntimeObservationSource.LegacyHook));
        TestAssert.True(legacy.Diagnostics.Contains("RUNTIME_OBSERVATION_IGNORED_NON_NATIVE_SOURCE"), "legacy hook input was not diagnosed");
        TestAssert.Equal(RuntimeState.Running, legacy.Snapshot.State, "legacy hook input overrode native authority");

        var attention = engine.Apply(new ThreadAttentionObservation("thread", ThreadAttentionState.Unread, fresh.AddSeconds(2)));
        TestAssert.Equal(RuntimeState.Running, attention.Snapshot.State, "attention evidence changed active authority");
        var staleAttention = engine.Apply(new ThreadAttentionObservation("thread", ThreadAttentionState.Read, fresh.AddSeconds(1)));
        TestAssert.True(staleAttention.Diagnostics.Contains("STALE_NATIVE_ATTENTION_OBSERVATION_IGNORED"), "stale attention was not diagnosed");
        TestAssert.Equal(ThreadAttentionState.Unread, staleAttention.Snapshot.Threads[0].Attention, "stale attention overrode fresher evidence");
        return Task.CompletedTask;
    }

    public static Task FocusIsOrthogonalAndRehydrationIsDeterministic()
    {
        var engine = new RuntimeStateEngine();
        var timestamp = DateTimeOffset.Parse("2026-09-08T10:00:00Z");
        engine.Apply(new ThreadRuntimeObservation(
            "thread",
            ThreadRuntimeStatus.Active,
            focusHint: ThreadFocusHint.Focused,
            observedUtc: timestamp));
        engine.Apply(new ThreadAttentionObservation("thread", ThreadAttentionState.Read, timestamp));
        var exported = engine.Snapshot;

        var imported = RuntimeStateEngine.Import(exported);
        TestAssert.Equal(
            RuntimeContractJson.Serialize(exported),
            RuntimeContractJson.Serialize(imported.Snapshot),
            "snapshot export/import is not deterministic");

        var duplicate = imported.Apply(new ThreadRuntimeObservation(
            "thread",
            ThreadRuntimeStatus.Active,
            focusHint: ThreadFocusHint.NotFocused,
            observedUtc: timestamp));
        TestAssert.Equal(RuntimeState.Running, duplicate.Snapshot.State, "focus hint influenced runtime state");
        TestAssert.True(duplicate.Diagnostics.Contains("DUPLICATE_NATIVE_RUNTIME_OBSERVATION_IGNORED"), "rehydrated timestamp was not preserved");

        var tampered = new RuntimeSnapshot(
            exported.SchemaVersion,
            RuntimeState.Normal,
            exported.Threads.Select(thread => new ThreadSnapshot(
                thread.ThreadId,
                thread.RuntimeStatus,
                thread.ActiveFlags,
                thread.LastObservedUtc,
                RuntimeState.Normal,
                thread.Attention,
                thread.LastAttentionObservedUtc)).ToImmutableArray(),
            exported.Health);
        var rebuilt = RuntimeStateEngine.Import(tampered);
        TestAssert.Equal(RuntimeState.Running, rebuilt.Snapshot.State, "rehydration trusted tampered derived state");
        return Task.CompletedTask;
    }
}
