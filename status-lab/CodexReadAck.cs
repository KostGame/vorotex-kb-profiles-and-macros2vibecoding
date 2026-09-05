namespace Vorotex.K15.StatusLab;

internal sealed record CodexCompletionKey(string SessionId, string ThreadId, string TurnId,
    long Generation, Guid RuntimeEpoch, DateTimeOffset CompletedUtc);

internal sealed record CodexReadAckEvidence(CodexCompletionKey Completion, string Host,
    DateTimeOffset HasUnreadUtc, DateTimeOffset FirstNoUnreadUtc, DateTimeOffset SecondNoUnreadUtc);

// Polling and I/O belong to the runtime. The reducer independently validates
// causal evidence against its current completion before changing any state.
internal sealed class CodexReadAckObserver(ICodexUnreadStateReader reader, string host)
{
    internal const int MaxCompletions = 256;
    private sealed class Observation
    {
        public DateTimeOffset? HasUnreadUtc;
        public DateTimeOffset? FirstNoUnreadUtc;
    }
    private readonly Dictionary<CodexCompletionKey, Observation> _observations = new();
    private readonly Dictionary<CodexCompletionKey, CodexReadAckEvidence> _ready = new();
    private DateTimeOffset _nextPollUtc;
    private DateTimeOffset _lastFinishedUtc;

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
        var snapshot = reader.Read(nowUtc);
        if (snapshot.Host != host || snapshot.StartedUtc < nowUtc || snapshot.FinishedUtc < snapshot.StartedUtc ||
            snapshot.FinishedUtc - snapshot.StartedUtc > TimeSpan.FromSeconds(2) || snapshot.StartedUtc <= _lastFinishedUtc)
        { _observations.Clear(); return _ready.Values.ToArray(); }
        _lastFinishedUtc = snapshot.FinishedUtc;
        foreach (var key in completions)
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
                    // Gaps break the causal chain. Recovery must observe fresh unread.
                    _observations.Remove(key);
                    break;
            }
        }
        return _ready.Values.ToArray();
    }

    public void ConfirmApplied(CodexReadAckEvidence evidence) => _ready.Remove(evidence.Completion);
}
