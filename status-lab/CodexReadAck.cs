namespace Vorotex.K15.StatusLab;

internal sealed record CodexCompletionKey(string SessionId, string ThreadId, string TurnId,
    long Generation, Guid RuntimeEpoch, DateTimeOffset CompletedUtc, string SourceInstanceId = "");

internal sealed record CodexReadAckEvidence(CodexCompletionKey Completion, string Host,
    DateTimeOffset HasUnreadUtc, DateTimeOffset FirstNoUnreadUtc, DateTimeOffset SecondNoUnreadUtc);

// Polling and I/O belong to the runtime. The reducer independently validates
// causal evidence against its current completion before changing any state.
internal sealed class CodexReadAckObserver(ICodexUnreadSourceRegistry sources, string host)
{
    internal CodexReadAckObserver(ICodexUnreadStateReader reader, string host)
        : this(new SingleCodexUnreadSourceRegistry(reader), host)
    {
    }

    internal const int MaxCompletions = 256;
    private sealed class Observation
    {
        public DateTimeOffset? HasUnreadUtc;
        public DateTimeOffset? FirstNoUnreadUtc;
    }
    private readonly Dictionary<CodexCompletionKey, Observation> _observations = new();
    private readonly Dictionary<CodexCompletionKey, CodexReadAckEvidence> _ready = new();
    private DateTimeOffset _nextPollUtc;
    private readonly Dictionary<string, DateTimeOffset> _lastFinishedBySource = new(StringComparer.Ordinal);

    public IReadOnlyList<CodexReadAckEvidence> Poll(IReadOnlyList<CodexCompletionKey> completions, DateTimeOffset nowUtc)
    {
        // Overflow is uncertainty, not permission to select an arbitrary subset.
        if (completions.Count == 0)
        { _observations.Clear(); _ready.Clear(); return Array.Empty<CodexReadAckEvidence>(); }
        var keys = completions.ToHashSet();
        foreach (var old in _observations.Keys.Where(key => !keys.Contains(key)).ToArray()) _observations.Remove(old);
        foreach (var old in _ready.Keys.Where(key => !keys.Contains(key)).ToArray()) _ready.Remove(old);
        if (completions.Count > MaxCompletions)
        { _observations.Clear(); return _ready.Values.ToArray(); }
        if (nowUtc < _nextPollUtc || keys.All(key => _ready.ContainsKey(key))) return _ready.Values.ToArray();
        _nextPollUtc = nowUtc.AddSeconds(1);
        var snapshotBySource = new Dictionary<string, CodexUnreadSnapshot>(StringComparer.Ordinal);
        foreach (var sourceInstanceId in completions.Select(key => key.SourceInstanceId).Distinct(StringComparer.Ordinal))
            snapshotBySource[sourceInstanceId] = sources.Read(sourceInstanceId, nowUtc);

        foreach (var group in completions.GroupBy(key => key.SourceInstanceId, StringComparer.Ordinal))
        {
            var sourceInstanceId = group.Key;
            var snapshot = snapshotBySource[sourceInstanceId];
            var previousFinished = _lastFinishedBySource.TryGetValue(sourceInstanceId, out var previous)
                ? previous
                : DateTimeOffset.MinValue;
            var validEnvelope = snapshot.Host == host && snapshot.StartedUtc >= nowUtc &&
                snapshot.FinishedUtc >= snapshot.StartedUtc &&
                snapshot.FinishedUtc - snapshot.StartedUtc <= TimeSpan.FromSeconds(2) &&
                snapshot.StartedUtc > previousFinished;
            if (!validEnvelope)
            {
                foreach (var key in group) _observations.Remove(key);
                continue;
            }

            _lastFinishedBySource[sourceInstanceId] = snapshot.FinishedUtc;
            foreach (var key in group)
            {
                if (_ready.ContainsKey(key)) continue;
                if (snapshot.StartedUtc <= key.CompletedUtc) { _observations.Remove(key); continue; }
                if (!_observations.TryGetValue(key, out var observation))
                    _observations[key] = observation = new();
                switch (snapshot.ForThread(key.ThreadId))
                {
                    case CodexUnreadState.HasUnread:
                        observation.HasUnreadUtc = snapshot.FinishedUtc;
                        observation.FirstNoUnreadUtc = null;
                        break;
                    case CodexUnreadState.NoUnread when observation.HasUnreadUtc is DateTimeOffset unread && snapshot.StartedUtc > unread:
                        if (observation.FirstNoUnreadUtc is DateTimeOffset first && snapshot.StartedUtc > first)
                        {
                            _ready[key] = new(key, host, unread, first, snapshot.FinishedUtc);
                            _observations.Remove(key);
                        }
                        else observation.FirstNoUnreadUtc = snapshot.FinishedUtc;
                        break;
                    default:
                        // Gaps break causal chain for this source only.
                        _observations.Remove(key);
                        break;
                }
            }
        }
        return _ready.Values.ToArray();
    }

    public void ConfirmApplied(CodexReadAckEvidence evidence) => _ready.Remove(evidence.Completion);
}
