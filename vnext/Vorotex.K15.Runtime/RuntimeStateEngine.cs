using System.Collections.Immutable;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

/// <summary>
/// Immutable input describing one native thread-status observation.
/// </summary>
public sealed record ThreadRuntimeObservation
{
    public ThreadRuntimeObservation(
        string threadId,
        ThreadRuntimeStatus runtimeStatus,
        IEnumerable<ThreadActiveFlag>? activeFlags = null,
        DateTimeOffset? observedUtc = null,
        RuntimeObservationSource source = RuntimeObservationSource.Native,
        ThreadFocusHint focusHint = ThreadFocusHint.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ThreadId = threadId;
        RuntimeStatus = runtimeStatus;
        ActiveFlags = CanonicalizeFlags(activeFlags);
        ObservedUtc = observedUtc;
        Source = source;
        FocusHint = focusHint;
    }

    public string ThreadId { get; }
    public ThreadRuntimeStatus RuntimeStatus { get; }
    public ImmutableArray<ThreadActiveFlag> ActiveFlags { get; }
    public DateTimeOffset? ObservedUtc { get; }
    public RuntimeObservationSource Source { get; }
    public ThreadFocusHint FocusHint { get; }

    private static ImmutableArray<ThreadActiveFlag> CanonicalizeFlags(
        IEnumerable<ThreadActiveFlag>? activeFlags) =>
        activeFlags is null
            ? ImmutableArray<ThreadActiveFlag>.Empty
            : activeFlags
                .Distinct()
                .OrderBy(GetFlagOrder)
                .ThenBy(flag => (int)flag)
                .ToImmutableArray();

    private static int GetFlagOrder(ThreadActiveFlag flag) => flag switch
    {
        ThreadActiveFlag.WaitingOnApproval => 0,
        ThreadActiveFlag.WaitingOnUserInput => 1,
        ThreadActiveFlag.Unknown => 2,
        _ => 3,
    };
}

/// <summary>
/// Immutable read/unread evidence. It has its own timestamp and cannot be
/// inferred from a runtime-active transition.
/// </summary>
public sealed record ThreadAttentionObservation
{
    public ThreadAttentionObservation(
        string threadId,
        ThreadAttentionState attention,
        DateTimeOffset? observedUtc = null,
        RuntimeObservationSource source = RuntimeObservationSource.Native)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ThreadId = threadId;
        Attention = attention;
        ObservedUtc = observedUtc;
        Source = source;
    }

    public string ThreadId { get; }
    public ThreadAttentionState Attention { get; }
    public DateTimeOffset? ObservedUtc { get; }
    public RuntimeObservationSource Source { get; }
}

public sealed record StateEngineResult(
    RuntimeSnapshot Snapshot,
    ImmutableArray<string> Diagnostics)
{
    public bool HasDiagnostics => !Diagnostics.IsEmpty;
}

/// <summary>
/// Deterministic, in-process state reducer for native thread status and
/// independent attention evidence. The reducer has no timers or wall-clock
/// reads; every transition is caused by an explicit observation.
/// </summary>
public sealed class RuntimeStateEngine
{
    private readonly object _gate = new();
    private readonly string _runtimeVersion;
    private Dictionary<string, ThreadState> _threads;
    private RuntimeSnapshot _snapshot;

    public RuntimeStateEngine(
        string runtimeVersion = RuntimeContractMetadata.CurrentRuntimeVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);
        _runtimeVersion = runtimeVersion;
        _threads = new Dictionary<string, ThreadState>(StringComparer.Ordinal);
        _snapshot = RuntimeSnapshot.CreateInitial(runtimeVersion);
    }

    private RuntimeStateEngine(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Health.RuntimeVersion);
        if (snapshot.SchemaVersion != RuntimeContractMetadata.CurrentSchemaVersion)
        {
            throw new ArgumentException(
                $"Unsupported runtime snapshot schema: {snapshot.SchemaVersion}.",
                nameof(snapshot));
        }

        _runtimeVersion = snapshot.Health.RuntimeVersion;
        _threads = new Dictionary<string, ThreadState>(StringComparer.Ordinal);
        foreach (var thread in snapshot.Threads.IsDefault
                     ? ImmutableArray<ThreadSnapshot>.Empty
                     : snapshot.Threads)
        {
            if (!_threads.TryAdd(thread.ThreadId, ThreadState.FromSnapshot(thread)))
            {
                throw new ArgumentException(
                    $"Runtime snapshot contains duplicate thread id '{thread.ThreadId}'.",
                    nameof(snapshot));
            }
        }

        _snapshot = BuildSnapshot();
    }

    public RuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public static RuntimeStateEngine Import(RuntimeSnapshot snapshot) =>
        new(snapshot);

    public StateEngineResult Apply(ThreadRuntimeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            var diagnostics = ImmutableArray.CreateBuilder<string>();
            if (observation.Source != RuntimeObservationSource.Native)
            {
                diagnostics.Add("RUNTIME_OBSERVATION_IGNORED_NON_NATIVE_SOURCE");
                return Result(diagnostics);
            }

            var current = GetOrCreate(observation.ThreadId);
            var candidate = current with
            {
                RuntimeStatus = observation.RuntimeStatus,
                ActiveFlags = observation.ActiveFlags,
                LastObservedUtc = observation.ObservedUtc,
            };

            var decision = CompareEvidence(
                current.LastObservedUtc,
                current.RuntimeFingerprint,
                observation.ObservedUtc,
                RuntimeFingerprint(observation));
            if (decision > 0)
            {
                candidate = current;
                diagnostics.Add("STALE_NATIVE_RUNTIME_OBSERVATION_IGNORED");
            }
            else if (decision == 0)
            {
                candidate = current;
                diagnostics.Add("DUPLICATE_NATIVE_RUNTIME_OBSERVATION_IGNORED");
            }
            else
            {
                if (observation.RuntimeStatus == ThreadRuntimeStatus.Unknown)
                {
                    diagnostics.Add("UNKNOWN_NATIVE_RUNTIME_STATUS");
                }

                if (observation.RuntimeStatus == ThreadRuntimeStatus.NotLoaded)
                {
                    diagnostics.Add("NOT_LOADED_NO_LIVE_RUNTIME_AUTHORITY");
                }

                if (observation.RuntimeStatus == ThreadRuntimeStatus.Idle
                    && current.Attention == ThreadAttentionState.Unknown)
                {
                    diagnostics.Add("MISSING_NATIVE_ATTENTION_FOR_IDLE");
                }

                if (observation.ActiveFlags.Contains(ThreadActiveFlag.Unknown))
                {
                    diagnostics.Add("UNKNOWN_NATIVE_ACTIVE_FLAG");
                }

                candidate = candidate with
                {
                    RuntimeFingerprint = RuntimeFingerprint(observation),
                };
            }

            _threads[observation.ThreadId] = candidate;
            _snapshot = BuildSnapshot();
            return Result(diagnostics);
        }
    }

    public StateEngineResult Apply(ThreadAttentionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            var diagnostics = ImmutableArray.CreateBuilder<string>();
            if (observation.Source != RuntimeObservationSource.Native)
            {
                diagnostics.Add("ATTENTION_OBSERVATION_IGNORED_NON_NATIVE_SOURCE");
                return Result(diagnostics);
            }

            var current = GetOrCreate(observation.ThreadId);
            var fingerprint = AttentionFingerprint(observation.Attention);
            var decision = CompareEvidence(
                current.LastAttentionObservedUtc,
                current.AttentionFingerprint,
                observation.ObservedUtc,
                fingerprint);
            if (decision > 0)
            {
                diagnostics.Add("STALE_NATIVE_ATTENTION_OBSERVATION_IGNORED");
            }
            else if (decision == 0)
            {
                diagnostics.Add("DUPLICATE_NATIVE_ATTENTION_OBSERVATION_IGNORED");
            }
            else
            {
                if (observation.Attention == ThreadAttentionState.Unknown)
                {
                    diagnostics.Add("UNKNOWN_NATIVE_ATTENTION");
                }

                current = current with
                {
                    Attention = observation.Attention,
                    LastAttentionObservedUtc = observation.ObservedUtc,
                    AttentionFingerprint = fingerprint,
                };
                _threads[observation.ThreadId] = current;
            }

            _snapshot = BuildSnapshot();
            return Result(diagnostics);
        }
    }

    private StateEngineResult Result(ImmutableArray<string>.Builder diagnostics) =>
        new(_snapshot, diagnostics.ToImmutable());

    private ThreadState GetOrCreate(string threadId)
    {
        if (_threads.TryGetValue(threadId, out var state))
        {
            return state;
        }

        state = ThreadState.Create(threadId);
        _threads.Add(threadId, state);
        return state;
    }

    private RuntimeSnapshot BuildSnapshot()
    {
        var threads = _threads.Values
            .OrderBy(thread => thread.ThreadId, StringComparer.Ordinal)
            .Select(thread => thread.ToSnapshot())
            .ToImmutableArray();

        var state = Aggregate(threads);
        return new RuntimeSnapshot(
            RuntimeContractMetadata.CurrentSchemaVersion,
            state,
            threads,
            RuntimeHealthSnapshot.Healthy(_runtimeVersion));
    }

    private static RuntimeState Aggregate(ImmutableArray<ThreadSnapshot> threads)
    {
        if (threads.IsDefaultOrEmpty)
        {
            return RuntimeState.Normal;
        }

        if (threads.Any(thread => thread.State == RuntimeState.Unknown))
        {
            return RuntimeState.Unknown;
        }

        foreach (var state in new[]
                 {
                     RuntimeState.Waiting,
                     RuntimeState.Blocked,
                     RuntimeState.DonePendingAttention,
                     RuntimeState.Running,
                     RuntimeState.Normal,
                 })
        {
            if (threads.Any(thread => thread.State == state))
            {
                return state;
            }
        }

        return RuntimeState.Unknown;
    }

    private static int CompareEvidence(
        DateTimeOffset? currentTimestamp,
        string currentFingerprint,
        DateTimeOffset? incomingTimestamp,
        string incomingFingerprint)
    {
        if (currentTimestamp.HasValue && !incomingTimestamp.HasValue)
        {
            return 1;
        }

        if (!currentTimestamp.HasValue && incomingTimestamp.HasValue)
        {
            return -1;
        }

        if (!currentTimestamp.HasValue && !incomingTimestamp.HasValue)
        {
            // Without timestamps, arrival order is the only available source
            // of sequencing. The reducer remains deterministic for a given
            // event stream while still allowing ordinary live transitions.
            return string.Equals(currentFingerprint, incomingFingerprint, StringComparison.Ordinal)
                ? 0
                : -1;
        }

        if (currentTimestamp.HasValue && incomingTimestamp.HasValue)
        {
            var timestampComparison = incomingTimestamp.Value.CompareTo(currentTimestamp.Value);
            if (timestampComparison != 0)
            {
                return timestampComparison < 0 ? 1 : -1;
            }
        }

        return string.Equals(currentFingerprint, incomingFingerprint, StringComparison.Ordinal)
            ? 0
            : string.CompareOrdinal(incomingFingerprint, currentFingerprint) < 0 ? 1 : -1;
    }

    private static string RuntimeFingerprint(ThreadRuntimeObservation observation) =>
        $"{(int)observation.RuntimeStatus}:{string.Join(',', observation.ActiveFlags.Select(flag => (int)flag))}";

    private static string AttentionFingerprint(ThreadAttentionState attention) =>
        ((int)attention).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ThreadState(
        string ThreadId,
        ThreadRuntimeStatus RuntimeStatus,
        ImmutableArray<ThreadActiveFlag> ActiveFlags,
        DateTimeOffset? LastObservedUtc,
        string RuntimeFingerprint,
        ThreadAttentionState Attention,
        DateTimeOffset? LastAttentionObservedUtc,
        string AttentionFingerprint)
    {
        public static ThreadState Create(string threadId) => new(
            threadId,
            ThreadRuntimeStatus.Unknown,
            ImmutableArray<ThreadActiveFlag>.Empty,
            null,
            string.Empty,
            ThreadAttentionState.Unknown,
            null,
            string.Empty);

        public static ThreadState FromSnapshot(ThreadSnapshot snapshot)
        {
            var runtimeObservation = new ThreadRuntimeObservation(
                snapshot.ThreadId,
                snapshot.RuntimeStatus,
                snapshot.ActiveFlags,
                snapshot.LastObservedUtc);
            return new ThreadState(
                snapshot.ThreadId,
                snapshot.RuntimeStatus,
                snapshot.ActiveFlags,
                snapshot.LastObservedUtc,
                RuntimeStateEngine.RuntimeFingerprint(runtimeObservation),
                snapshot.Attention,
                snapshot.LastAttentionObservedUtc,
                RuntimeStateEngine.AttentionFingerprint(snapshot.Attention));
        }

        public ThreadSnapshot ToSnapshot()
        {
            var state = Evaluate(RuntimeStatus, ActiveFlags, Attention);
            return ThreadSnapshot.Create(
                ThreadId,
                RuntimeStatus,
                ActiveFlags,
                LastObservedUtc,
                state,
                Attention,
                LastAttentionObservedUtc);
        }

        private static RuntimeState Evaluate(
            ThreadRuntimeStatus runtimeStatus,
            ImmutableArray<ThreadActiveFlag> activeFlags,
            ThreadAttentionState attention)
        {
            return runtimeStatus switch
            {
                ThreadRuntimeStatus.Active when activeFlags.Contains(ThreadActiveFlag.Unknown) => RuntimeState.Unknown,
                ThreadRuntimeStatus.Active when activeFlags.Contains(ThreadActiveFlag.WaitingOnApproval)
                    || activeFlags.Contains(ThreadActiveFlag.WaitingOnUserInput) => RuntimeState.Waiting,
                ThreadRuntimeStatus.Active => RuntimeState.Running,
                ThreadRuntimeStatus.SystemError => RuntimeState.Blocked,
                ThreadRuntimeStatus.Idle when attention == ThreadAttentionState.Unread => RuntimeState.DonePendingAttention,
                ThreadRuntimeStatus.Idle when attention == ThreadAttentionState.Read => RuntimeState.Normal,
                _ => RuntimeState.Unknown,
            };
        }
    }
}
