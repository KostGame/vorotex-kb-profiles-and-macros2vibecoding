namespace Vorotex.K15.StatusLab;

internal enum K15NormalizedState
{
    Normal,
    Running,
    Waiting,
    DonePendingAttention,
    Error
}

internal enum CodexLivenessState
{
    Alive,
    NotRunning,
    Unknown
}

// Reserved for a future proven Codex host signal. Unknown deliberately has no
// destructive effect on the attention ledger.
internal interface ICodexLivenessProvider
{
    CodexLivenessState GetLiveness();
}

internal sealed record StatusInputEvent(
    DateTimeOffset TimestampUtc,
    string Source,
    string EventName,
    uint? NotificationId = null,
    string PackageFamilyName = "",
    bool ErrorHint = false,
    string SessionId = "",
    string TurnId = "",
    string Cwd = "",
    string SchemaVersion = "",
    string Decision = "",
    string RequestId = "",
    string ThreadId = "",
    string ItemId = "");

internal sealed record StateTransition(
    K15NormalizedState Previous,
    K15NormalizedState Current,
    string Reason,
    DateTimeOffset TimestampUtc);

internal sealed record CodexAttentionSnapshot(
    int RunningCount,
    int ApprovalWaitingCount,
    int DoneUnreadCount,
    int ActiveTaskSessionCount,
    int EndedSessionCount,
    K15NormalizedState AggregateState,
    DateTimeOffset? NoRunningSinceUtc,
    DateTimeOffset? StaleResetDueUtc);

internal sealed class StateReducer
{
    private const string LegacySessionId = "__legacy__";

    private sealed class SessionRuntime
    {
        public required string Id { get; init; }
        public string ThreadId { get; set; } = string.Empty;
        public string TurnId { get; set; } = string.Empty;
        public string Cwd { get; set; } = string.Empty;
        public bool Internal { get; set; }
        public bool Ended { get; set; }
        public K15NormalizedState State { get; set; } = K15NormalizedState.Normal;
        public DateTimeOffset LastActivityUtc { get; set; }
        public DateTimeOffset? LastPermissionUtc { get; set; }
        public DateTimeOffset? LastStopUtc { get; set; }
        public DateTimeOffset? DoneEnteredUtc { get; set; }
        public DateTimeOffset? AcknowledgedUtc { get; set; }
    }

    private readonly TimeSpan? _staleAttentionTimeout;
    private readonly Dictionary<string, SessionRuntime> _sessions = new(StringComparer.Ordinal);
    private DateTimeOffset? _noRunningSinceUtc;

    public StateReducer(double staleAttentionTimeoutSeconds = 18000)
    {
        if (staleAttentionTimeoutSeconds < 0 || staleAttentionTimeoutSeconds > 259200)
            throw new ArgumentOutOfRangeException(nameof(staleAttentionTimeoutSeconds));

        _staleAttentionTimeout = staleAttentionTimeoutSeconds == 0
            ? null
            : TimeSpan.FromSeconds(staleAttentionTimeoutSeconds);
    }

    public K15NormalizedState State { get; private set; } = K15NormalizedState.Normal;
    public string? FocusedSessionId { get; private set; }
    public string FocusedCwd => FocusedSessionId is not null && _sessions.TryGetValue(FocusedSessionId, out var session)
        ? session.Cwd
        : string.Empty;
    public int ActiveTaskSessionCount => _sessions.Values.Count(session => !session.Internal && !session.Ended);
    public CodexLivenessState Liveness => CodexLivenessState.Unknown;

    public CodexAttentionSnapshot Snapshot => CreateSnapshot();

    public StateTransition? Apply(StatusInputEvent input)
    {
        if (input.Source.Equals("codex_hook", StringComparison.Ordinal))
            return ApplyCodex(input);

        if (input.Source.Equals("codex_stdio_bridge", StringComparison.Ordinal))
            return ApplyApprovalResolution(input);

        // Windows toasts are retained as diagnostics only. They are not an ACK
        // signal and must never erase session attention.
        return null;
    }

    public void Rehydrate(IEnumerable<StatusInputEvent> events)
    {
        ResetAll();
        foreach (var input in events
                     .Where(input => input.Source.Equals("codex_hook", StringComparison.Ordinal) ||
                                     input.Source.Equals("codex_stdio_bridge", StringComparison.Ordinal))
                     .OrderBy(input => input.TimestampUtc))
        {
            Apply(input);
        }
    }

    public StateTransition? Tick(DateTimeOffset nowUtc)
    {
        var snapshot = CreateSnapshot();
        if (_staleAttentionTimeout is not TimeSpan timeout ||
            snapshot.RunningCount != 0 ||
            (snapshot.ApprovalWaitingCount == 0 && snapshot.DoneUnreadCount == 0) ||
            _noRunningSinceUtc is not DateTimeOffset idleSince ||
            nowUtc - idleSince < timeout)
        {
            return null;
        }

        foreach (var session in _sessions.Values.Where(session => !session.Internal &&
                     session.State is K15NormalizedState.Waiting or K15NormalizedState.DonePendingAttention))
        {
            session.State = K15NormalizedState.Normal;
            session.LastPermissionUtc = null;
            session.LastStopUtc = null;
            session.DoneEnteredUtc = null;
            session.AcknowledgedUtc = nowUtc;
        }

        _noRunningSinceUtc = null;
        return RecomputeAggregate("stale_attention_timeout", nowUtc);
    }

    // MVP tray/control-center ACK clears all outstanding attention. The session
    // ledger remains intentionally ready for a future Acknowledge(sessionId).
    public StateTransition? Acknowledge(DateTimeOffset timestampUtc, string reason = "manual_acknowledge")
    {
        foreach (var session in _sessions.Values.Where(session => !session.Internal &&
                     session.State is K15NormalizedState.Waiting or K15NormalizedState.DonePendingAttention))
        {
            session.State = K15NormalizedState.Normal;
            session.LastPermissionUtc = null;
            session.LastStopUtc = null;
            session.DoneEnteredUtc = null;
            session.AcknowledgedUtc = timestampUtc;
        }

        return RecomputeAggregate(reason, timestampUtc);
    }

    public StateTransition? Acknowledge(string sessionId, DateTimeOffset timestampUtc, string reason = "session_acknowledge")
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.Internal ||
            session.State is not (K15NormalizedState.Waiting or K15NormalizedState.DonePendingAttention))
        {
            return null;
        }

        session.State = K15NormalizedState.Normal;
        session.LastPermissionUtc = null;
        session.LastStopUtc = null;
        session.DoneEnteredUtc = null;
        session.AcknowledgedUtc = timestampUtc;
        return RecomputeAggregate(reason, timestampUtc);
    }

    private StateTransition? ApplyCodex(StatusInputEvent input)
    {
        var session = GetOrCreateSession(input);
        if (!string.IsNullOrWhiteSpace(input.Cwd))
            session.Cwd = input.Cwd;
        if (!string.IsNullOrWhiteSpace(input.ThreadId))
            session.ThreadId = input.ThreadId;
        if (!string.IsNullOrWhiteSpace(input.TurnId))
            session.TurnId = input.TurnId;
        session.Internal = session.Internal || IsInternalCwd(session.Cwd);
        session.LastActivityUtc = input.TimestampUtc;

        if (input.EventName == "SessionEnd")
        {
            session.Ended = true;
            // Completion is not a read receipt: retain DONE_UNREAD across end.
            if (session.State is not K15NormalizedState.DonePendingAttention)
            {
                session.State = K15NormalizedState.Normal;
                session.LastPermissionUtc = null;
            }

            if (string.Equals(FocusedSessionId, session.Id, StringComparison.Ordinal))
                SelectFallbackFocus();
            return RecomputeAggregate("codex_session_end", input.TimestampUtc);
        }

        if (session.Internal)
            return null;

        session.Ended = false;
        switch (input.EventName)
        {
            case "UserPromptSubmit":
                // A prompt in this same session is the MVP explicit return/ACK.
                session.State = K15NormalizedState.Running;
                session.LastPermissionUtc = null;
                session.LastStopUtc = null;
                session.DoneEnteredUtc = null;
                session.AcknowledgedUtc = input.TimestampUtc;
                FocusedSessionId = session.Id;
                return RecomputeAggregate("codex_user_prompt_submit", input.TimestampUtc);

            case "PermissionRequest":
                session.State = K15NormalizedState.Waiting;
                session.LastPermissionUtc = input.TimestampUtc;
                session.LastStopUtc = null;
                session.DoneEnteredUtc = null;
                FocusedSessionId = session.Id;
                return RecomputeAggregate("codex_permission_request", input.TimestampUtc);

            case "PreToolUse":
            case "PostToolUse":
                session.State = K15NormalizedState.Running;
                session.LastPermissionUtc = null;
                session.LastStopUtc = null;
                session.DoneEnteredUtc = null;
                FocusedSessionId = session.Id;
                return RecomputeAggregate(input.EventName == "PreToolUse" ? "codex_pre_tool_use" : "codex_post_tool_use", input.TimestampUtc);

            case "Stop":
                session.State = K15NormalizedState.DonePendingAttention;
                session.LastPermissionUtc = null;
                session.LastStopUtc = input.TimestampUtc;
                session.DoneEnteredUtc = input.TimestampUtc;
                FocusedSessionId = session.Id;
                return RecomputeAggregate("codex_stop", input.TimestampUtc);

            default:
                return null;
        }
    }

    private StateTransition? ApplyApprovalResolution(StatusInputEvent input)
    {
        if (input.EventName != "approval_resolved" ||
            input.SchemaVersion != "k15-codex-approval/v1" ||
            input.Decision is not ("accept" or "acceptForSession") ||
            string.IsNullOrWhiteSpace(input.RequestId))
        {
            // decline/cancel are intentionally observable decisions, but they
            // do not prove that Codex resumed execution and never map to RUNNING.
            return null;
        }

        var candidates = _sessions.Values
            .Where(session => !session.Internal && !session.Ended &&
                              session.State == K15NormalizedState.Waiting)
            .Where(session => MatchesApproval(session, input))
            .ToArray();
        if (candidates.Length != 1)
            return null;

        var session = candidates[0];
        session.State = K15NormalizedState.Running;
        session.LastPermissionUtc = null;
        session.LastStopUtc = null;
        session.DoneEnteredUtc = null;
        session.AcknowledgedUtc = input.TimestampUtc;
        FocusedSessionId = session.Id;
        return RecomputeAggregate("codex_approval_resolved", input.TimestampUtc);
    }

    private static bool MatchesApproval(SessionRuntime session, StatusInputEvent input)
    {
        var hasCorrelation = false;
        if (!string.IsNullOrWhiteSpace(input.TurnId))
        {
            hasCorrelation = true;
            if (!string.Equals(session.TurnId, input.TurnId, StringComparison.Ordinal))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(input.ThreadId))
        {
            hasCorrelation = true;
            if (!string.Equals(session.ThreadId, input.ThreadId, StringComparison.Ordinal) &&
                !string.Equals(session.Id, input.ThreadId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return hasCorrelation;
    }

    private SessionRuntime GetOrCreateSession(StatusInputEvent input)
    {
        var id = string.IsNullOrWhiteSpace(input.SessionId) ? LegacySessionId : input.SessionId;
        if (_sessions.TryGetValue(id, out var existing))
            return existing;

        var created = new SessionRuntime
        {
            Id = id,
            Cwd = input.Cwd,
            Internal = IsInternalCwd(input.Cwd),
            LastActivityUtc = input.TimestampUtc
        };
        _sessions[id] = created;
        return created;
    }

    private void SelectFallbackFocus()
    {
        FocusedSessionId = _sessions.Values
            .Where(session => !session.Internal && !session.Ended)
            .OrderByDescending(session => session.LastActivityUtc)
            .Select(session => session.Id)
            .FirstOrDefault();
    }

    private CodexAttentionSnapshot CreateSnapshot()
    {
        var sessions = _sessions.Values.Where(session => !session.Internal).ToArray();
        var running = sessions.Count(session => !session.Ended && session.State == K15NormalizedState.Running);
        var waiting = sessions.Count(session => !session.Ended && session.State == K15NormalizedState.Waiting);
        var done = sessions.Count(session => session.State == K15NormalizedState.DonePendingAttention);
        var aggregate = waiting > 0 ? K15NormalizedState.Waiting :
            done > 0 ? K15NormalizedState.DonePendingAttention :
            running > 0 ? K15NormalizedState.Running : K15NormalizedState.Normal;
        return new CodexAttentionSnapshot(
            running,
            waiting,
            done,
            sessions.Count(session => !session.Ended),
            sessions.Count(session => session.Ended),
            aggregate,
            _noRunningSinceUtc,
            _staleAttentionTimeout is TimeSpan timeout && _noRunningSinceUtc is DateTimeOffset since
                ? since + timeout
                : null);
    }

    private StateTransition? RecomputeAggregate(string reason, DateTimeOffset timestampUtc)
    {
        var before = State;
        var running = _sessions.Values.Count(session => !session.Internal && !session.Ended && session.State == K15NormalizedState.Running);
        if (running > 0)
            _noRunningSinceUtc = null;
        else if (_noRunningSinceUtc is null && _sessions.Values.Any(session => !session.Internal &&
                     session.State is K15NormalizedState.Waiting or K15NormalizedState.DonePendingAttention))
            _noRunningSinceUtc = timestampUtc;

        var next = CreateSnapshot().AggregateState;
        State = next;
        return before == next ? null : new StateTransition(before, next, reason, timestampUtc);
    }

    private void ResetAll()
    {
        _sessions.Clear();
        FocusedSessionId = null;
        State = K15NormalizedState.Normal;
        _noRunningSinceUtc = null;
    }

    internal static bool IsInternalCwd(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return false;

        var normalized = cwd.Replace('/', '\\').TrimEnd('\\');
        return normalized.Contains("\\.codex-agentloop\\memories", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("\\.codex\\memories", StringComparison.OrdinalIgnoreCase);
    }
}
