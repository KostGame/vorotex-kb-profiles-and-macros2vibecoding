namespace Vorotex.K15.StatusLab;

internal enum K15NormalizedState
{
    Normal,
    Running,
    Waiting,
    DonePendingAttention,
    Error
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
    string Cwd = "");

internal sealed record StateTransition(
    K15NormalizedState Previous,
    K15NormalizedState Current,
    string Reason,
    DateTimeOffset TimestampUtc);

internal sealed class StateReducer
{
    private static readonly TimeSpan NotificationCorrelationWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PreHookNotificationWindow = TimeSpan.FromSeconds(2);
    private const string LegacySessionId = "__legacy__";

    private sealed class SessionRuntime
    {
        public required string Id { get; init; }
        public string Cwd { get; set; } = string.Empty;
        public bool Internal { get; set; }
        public bool Ended { get; set; }
        public K15NormalizedState State { get; set; } = K15NormalizedState.Normal;
        public DateTimeOffset LastActivityUtc { get; set; }
        public DateTimeOffset? LastPermissionUtc { get; set; }
        public DateTimeOffset? LastStopUtc { get; set; }
        public DateTimeOffset? DoneEnteredUtc { get; set; }
    }

    private readonly TimeSpan? _doneAttentionTimeout;
    private readonly HashSet<uint> _waitingNotificationIds = new();
    private readonly HashSet<uint> _doneNotificationIds = new();
    private readonly Dictionary<uint, DateTimeOffset> _recentOpenAiAdds = new();
    private readonly Dictionary<string, SessionRuntime> _sessions = new(StringComparer.Ordinal);

    public StateReducer(double doneAttentionTimeoutSeconds = 15)
    {
        if (doneAttentionTimeoutSeconds < 0 || doneAttentionTimeoutSeconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(doneAttentionTimeoutSeconds));

        _doneAttentionTimeout = doneAttentionTimeoutSeconds == 0
            ? null
            : TimeSpan.FromSeconds(doneAttentionTimeoutSeconds);
    }

    public K15NormalizedState State { get; private set; } = K15NormalizedState.Normal;
    public string? FocusedSessionId { get; private set; }
    public string FocusedCwd => FocusedSessionId is not null && _sessions.TryGetValue(FocusedSessionId, out var session)
        ? session.Cwd
        : string.Empty;

    public int ActiveTaskSessionCount => _sessions.Values.Count(session => !session.Internal && !session.Ended);

    public StateTransition? Apply(StatusInputEvent input)
    {
        PruneRecentNotifications(input.TimestampUtc);

        if (input.Source.Equals("codex_hook", StringComparison.Ordinal))
            return ApplyCodex(input);

        if (input.Source.Equals("windows_notification", StringComparison.Ordinal))
            return ApplyNotification(input);

        return null;
    }

    public void Rehydrate(IEnumerable<StatusInputEvent> events)
    {
        ResetAll();
        foreach (var input in events
                     .Where(input => input.Source.Equals("codex_hook", StringComparison.Ordinal))
                     .OrderBy(input => input.TimestampUtc))
        {
            ApplyCodex(input);
        }

        ResetNotificationTracking();
    }

    public StateTransition? Tick(DateTimeOffset nowUtc)
    {
        PruneRecentNotifications(nowUtc);
        if (_doneAttentionTimeout is not TimeSpan timeout ||
            State is not (K15NormalizedState.DonePendingAttention or K15NormalizedState.Error) ||
            GetFocusedSession() is not SessionRuntime focused ||
            focused.DoneEnteredUtc is not DateTimeOffset entered ||
            nowUtc - entered < timeout)
        {
            return null;
        }

        focused.State = K15NormalizedState.Normal;
        focused.LastStopUtc = null;
        focused.DoneEnteredUtc = null;
        ResetNotificationTracking();
        return SetState(K15NormalizedState.Normal, "done_attention_timeout", nowUtc);
    }

    public StateTransition? Acknowledge(DateTimeOffset timestampUtc, string reason = "manual_acknowledge")
    {
        if (GetFocusedSession() is SessionRuntime focused)
        {
            focused.State = K15NormalizedState.Normal;
            focused.LastPermissionUtc = null;
            focused.LastStopUtc = null;
            focused.DoneEnteredUtc = null;
        }

        ResetNotificationTracking();
        return SetState(K15NormalizedState.Normal, reason, timestampUtc);
    }

    private StateTransition? ApplyCodex(StatusInputEvent input)
    {
        var session = GetOrCreateSession(input);
        if (!string.IsNullOrWhiteSpace(input.Cwd))
            session.Cwd = input.Cwd;
        session.Internal = session.Internal || IsInternalCwd(session.Cwd);
        session.LastActivityUtc = input.TimestampUtc;

        if (input.EventName == "SessionEnd")
        {
            session.Ended = true;
            session.State = K15NormalizedState.Normal;
            session.LastPermissionUtc = null;
            session.LastStopUtc = null;
            session.DoneEnteredUtc = null;

            if (!string.Equals(FocusedSessionId, session.Id, StringComparison.Ordinal))
                return null;

            return SelectFallbackFocus(input.TimestampUtc, "codex_session_end");
        }

        if (session.Internal)
            return null;

        session.Ended = false;
        switch (input.EventName)
        {
            case "UserPromptSubmit":
                ResetSessionTransient(session);
                session.State = K15NormalizedState.Running;
                return FocusSession(session, "codex_user_prompt_submit", input.TimestampUtc);

            case "PermissionRequest":
                session.State = K15NormalizedState.Waiting;
                session.LastPermissionUtc = input.TimestampUtc;
                session.LastStopUtc = null;
                session.DoneEnteredUtc = null;
                var waitingTransition = FocusSession(session, "codex_permission_request", input.TimestampUtc);
                BindRecentNotificationToWaiting(input.TimestampUtc);
                return waitingTransition;

            case "PostToolUse":
                session.State = K15NormalizedState.Running;
                session.LastPermissionUtc = null;
                session.LastStopUtc = null;
                session.DoneEnteredUtc = null;
                _waitingNotificationIds.Clear();
                return FocusSession(session, "codex_post_tool_use", input.TimestampUtc);

            case "Stop":
                session.State = K15NormalizedState.DonePendingAttention;
                session.LastPermissionUtc = null;
                session.LastStopUtc = input.TimestampUtc;
                session.DoneEnteredUtc = input.TimestampUtc;
                _waitingNotificationIds.Clear();
                var doneTransition = FocusSession(session, "codex_stop", input.TimestampUtc);
                BindRecentNotificationToDone(input.TimestampUtc);
                return doneTransition;

            default:
                return null;
        }
    }

    private StateTransition? ApplyNotification(StatusInputEvent input)
    {
        if (!IsOpenAiPackage(input.PackageFamilyName) || input.NotificationId is not uint notificationId)
            return null;

        var focused = GetFocusedSession();
        if (input.EventName == "windows_notification_added")
        {
            _recentOpenAiAdds[notificationId] = input.TimestampUtc;

            if (focused is not null && State == K15NormalizedState.Waiting &&
                focused.LastPermissionUtc is DateTimeOffset permissionUtc &&
                WithinWindow(permissionUtc, input.TimestampUtc))
            {
                _waitingNotificationIds.Add(notificationId);
                return null;
            }

            if (focused is not null &&
                (State is K15NormalizedState.DonePendingAttention or K15NormalizedState.Error) &&
                focused.LastStopUtc is DateTimeOffset stopUtc &&
                WithinWindow(stopUtc, input.TimestampUtc))
            {
                _doneNotificationIds.Add(notificationId);
                return null;
            }

            return null;
        }

        if (input.EventName == "windows_notification_removed")
        {
            _recentOpenAiAdds.Remove(notificationId);

            if (_waitingNotificationIds.Remove(notificationId) &&
                State == K15NormalizedState.Waiting && _waitingNotificationIds.Count == 0 && focused is not null)
            {
                focused.State = K15NormalizedState.Running;
                focused.LastPermissionUtc = null;
                return SetState(K15NormalizedState.Running, "waiting_notification_resolved", input.TimestampUtc);
            }

            if (_doneNotificationIds.Remove(notificationId) &&
                (State is K15NormalizedState.DonePendingAttention or K15NormalizedState.Error) &&
                _doneNotificationIds.Count == 0 && focused is not null)
            {
                focused.State = K15NormalizedState.Normal;
                focused.LastStopUtc = null;
                focused.DoneEnteredUtc = null;
                return SetState(K15NormalizedState.Normal, "done_notification_resolved", input.TimestampUtc);
            }
        }

        return null;
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

    private StateTransition? FocusSession(SessionRuntime session, string reason, DateTimeOffset timestampUtc)
    {
        var focusChanged = !string.Equals(FocusedSessionId, session.Id, StringComparison.Ordinal);
        if (focusChanged)
        {
            FocusedSessionId = session.Id;
            ClearBoundNotifications();
        }

        var transition = SetState(session.State, reason, timestampUtc);
        if (transition is not null)
            return transition;

        return focusChanged
            ? new StateTransition(State, State, "codex_focus_changed", timestampUtc)
            : null;
    }

    private StateTransition? SelectFallbackFocus(DateTimeOffset timestampUtc, string reason)
    {
        var candidate = _sessions.Values
            .Where(session => !session.Internal && !session.Ended)
            .OrderByDescending(session => session.LastActivityUtc)
            .FirstOrDefault();

        ResetNotificationTracking();
        if (candidate is null)
        {
            var hadFocus = FocusedSessionId is not null;
            FocusedSessionId = null;
            var transition = SetState(K15NormalizedState.Normal, reason, timestampUtc);
            if (transition is not null)
                return transition;
            return hadFocus
                ? new StateTransition(State, State, reason, timestampUtc)
                : null;
        }

        var previousFocus = FocusedSessionId;
        FocusedSessionId = candidate.Id;
        var fallbackTransition = SetState(candidate.State, "codex_focus_fallback", timestampUtc);
        if (fallbackTransition is not null)
            return fallbackTransition;
        return !string.Equals(previousFocus, candidate.Id, StringComparison.Ordinal)
            ? new StateTransition(State, State, "codex_focus_fallback", timestampUtc)
            : null;
    }

    private SessionRuntime? GetFocusedSession() =>
        FocusedSessionId is not null && _sessions.TryGetValue(FocusedSessionId, out var session)
            ? session
            : null;

    private void BindRecentNotificationToWaiting(DateTimeOffset permissionUtc)
    {
        var candidateId = FindRecentUnboundNotification(permissionUtc);
        if (candidateId is uint id)
            _waitingNotificationIds.Add(id);
    }

    private void BindRecentNotificationToDone(DateTimeOffset stopUtc)
    {
        var candidateId = FindRecentUnboundNotification(stopUtc);
        if (candidateId is uint id)
            _doneNotificationIds.Add(id);
    }

    private uint? FindRecentUnboundNotification(DateTimeOffset hookUtc) =>
        _recentOpenAiAdds
            .Where(pair =>
                !_waitingNotificationIds.Contains(pair.Key) &&
                !_doneNotificationIds.Contains(pair.Key) &&
                pair.Value <= hookUtc &&
                hookUtc - pair.Value <= PreHookNotificationWindow)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => (uint?)pair.Key)
            .FirstOrDefault();

    private void PruneRecentNotifications(DateTimeOffset nowUtc)
    {
        foreach (var id in _recentOpenAiAdds
                     .Where(pair => nowUtc - pair.Value > NotificationCorrelationWindow)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _recentOpenAiAdds.Remove(id);
        }
    }

    private void ResetSessionTransient(SessionRuntime session)
    {
        session.LastPermissionUtc = null;
        session.LastStopUtc = null;
        session.DoneEnteredUtc = null;
        ResetNotificationTracking();
    }

    private void ClearBoundNotifications()
    {
        _waitingNotificationIds.Clear();
        _doneNotificationIds.Clear();
    }

    private void ResetNotificationTracking()
    {
        ClearBoundNotifications();
        _recentOpenAiAdds.Clear();
    }

    private void ResetAll()
    {
        _sessions.Clear();
        FocusedSessionId = null;
        State = K15NormalizedState.Normal;
        ResetNotificationTracking();
    }

    private StateTransition? SetState(K15NormalizedState next, string reason, DateTimeOffset timestampUtc)
    {
        if (State == next)
            return null;

        var previous = State;
        State = next;
        return new StateTransition(previous, next, reason, timestampUtc);
    }

    internal static bool IsInternalCwd(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return false;

        var normalized = cwd.Replace('/', '\\').TrimEnd('\\');
        return normalized.Contains("\\.codex-agentloop\\memories", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("\\.codex\\memories", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenAiPackage(string packageFamilyName) =>
        packageFamilyName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);

    private static bool WithinWindow(DateTimeOffset earlier, DateTimeOffset later)
    {
        var delta = later - earlier;
        return delta >= TimeSpan.Zero && delta <= NotificationCorrelationWindow;
    }
}
