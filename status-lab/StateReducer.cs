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
    bool ErrorHint = false);

internal sealed record StateTransition(
    K15NormalizedState Previous,
    K15NormalizedState Current,
    string Reason,
    DateTimeOffset TimestampUtc);

internal sealed class StateReducer
{
    private static readonly TimeSpan NotificationCorrelationWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PreHookNotificationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DoneAttentionTimeout = TimeSpan.FromSeconds(15);

    private readonly HashSet<uint> _waitingNotificationIds = new();
    private readonly HashSet<uint> _doneNotificationIds = new();
    private readonly Dictionary<uint, DateTimeOffset> _recentOpenAiAdds = new();

    private DateTimeOffset? _lastPermissionUtc;
    private DateTimeOffset? _lastStopUtc;
    private DateTimeOffset? _doneEnteredUtc;

    public K15NormalizedState State { get; private set; } = K15NormalizedState.Normal;

    public StateTransition? Apply(StatusInputEvent input)
    {
        PruneRecentNotifications(input.TimestampUtc);

        if (input.Source.Equals("codex_hook", StringComparison.Ordinal))
            return ApplyCodex(input);

        if (input.Source.Equals("windows_notification", StringComparison.Ordinal))
            return ApplyNotification(input);

        return null;
    }

    public StateTransition? Tick(DateTimeOffset nowUtc)
    {
        PruneRecentNotifications(nowUtc);

        if ((State == K15NormalizedState.DonePendingAttention || State == K15NormalizedState.Error) &&
            _doneEnteredUtc is DateTimeOffset entered &&
            nowUtc - entered >= DoneAttentionTimeout)
        {
            _doneNotificationIds.Clear();
            _lastStopUtc = null;
            _doneEnteredUtc = null;
            return SetState(K15NormalizedState.Normal, "done_attention_timeout", nowUtc);
        }

        return null;
    }

    public StateTransition? Acknowledge(DateTimeOffset timestampUtc, string reason = "manual_acknowledge")
    {
        ResetTracking();
        return SetState(K15NormalizedState.Normal, reason, timestampUtc);
    }

    private StateTransition? ApplyCodex(StatusInputEvent input)
    {
        switch (input.EventName)
        {
            case "UserPromptSubmit":
                ResetTracking();
                return SetState(K15NormalizedState.Running, "codex_user_prompt_submit", input.TimestampUtc);

            case "PermissionRequest":
                _lastPermissionUtc = input.TimestampUtc;
                BindRecentNotificationToWaiting(input.TimestampUtc);
                return SetState(K15NormalizedState.Waiting, "codex_permission_request", input.TimestampUtc);

            case "Stop":
                _waitingNotificationIds.Clear();
                _lastPermissionUtc = null;
                _lastStopUtc = input.TimestampUtc;
                _doneEnteredUtc = input.TimestampUtc;
                return SetState(K15NormalizedState.DonePendingAttention, "codex_stop", input.TimestampUtc);

            case "SessionEnd":
                ResetTracking();
                return SetState(K15NormalizedState.Normal, "codex_session_end", input.TimestampUtc);

            default:
                return null;
        }
    }

    private StateTransition? ApplyNotification(StatusInputEvent input)
    {
        if (!IsOpenAiPackage(input.PackageFamilyName) || input.NotificationId is not uint notificationId)
            return null;

        if (input.EventName == "windows_notification_added")
        {
            _recentOpenAiAdds[notificationId] = input.TimestampUtc;

            if (State == K15NormalizedState.Waiting &&
                _lastPermissionUtc is DateTimeOffset permissionUtc &&
                WithinWindow(permissionUtc, input.TimestampUtc))
            {
                _waitingNotificationIds.Add(notificationId);
                return null;
            }

            if ((State == K15NormalizedState.DonePendingAttention || State == K15NormalizedState.Error) &&
                _lastStopUtc is DateTimeOffset stopUtc &&
                WithinWindow(stopUtc, input.TimestampUtc))
            {
                _doneNotificationIds.Add(notificationId);
                if (input.ErrorHint)
                    return SetState(K15NormalizedState.Error, "openai_post_stop_error_notification", input.TimestampUtc);
                return null;
            }

            return null;
        }

        if (input.EventName == "windows_notification_removed")
        {
            _recentOpenAiAdds.Remove(notificationId);

            if (_waitingNotificationIds.Remove(notificationId) &&
                State == K15NormalizedState.Waiting &&
                _waitingNotificationIds.Count == 0)
            {
                return SetState(K15NormalizedState.Running, "waiting_notification_resolved", input.TimestampUtc);
            }

            if (_doneNotificationIds.Remove(notificationId) &&
                (State == K15NormalizedState.DonePendingAttention || State == K15NormalizedState.Error) &&
                _doneNotificationIds.Count == 0)
            {
                _lastStopUtc = null;
                _doneEnteredUtc = null;
                return SetState(K15NormalizedState.Normal, "done_notification_resolved", input.TimestampUtc);
            }
        }

        return null;
    }

    private void BindRecentNotificationToWaiting(DateTimeOffset permissionUtc)
    {
        var candidate = _recentOpenAiAdds
            .Where(pair =>
                !_waitingNotificationIds.Contains(pair.Key) &&
                !_doneNotificationIds.Contains(pair.Key) &&
                pair.Value <= permissionUtc &&
                permissionUtc - pair.Value <= PreHookNotificationWindow)
            .OrderByDescending(pair => pair.Value)
            .FirstOrDefault();

        if (candidate.Key != 0 || _recentOpenAiAdds.ContainsKey(0))
            _waitingNotificationIds.Add(candidate.Key);
    }

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

    private void ResetTracking()
    {
        _waitingNotificationIds.Clear();
        _doneNotificationIds.Clear();
        _recentOpenAiAdds.Clear();
        _lastPermissionUtc = null;
        _lastStopUtc = null;
        _doneEnteredUtc = null;
    }

    private StateTransition? SetState(K15NormalizedState next, string reason, DateTimeOffset timestampUtc)
    {
        if (State == next)
            return null;

        var previous = State;
        State = next;
        return new StateTransition(previous, next, reason, timestampUtc);
    }

    private static bool IsOpenAiPackage(string packageFamilyName) =>
        packageFamilyName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);

    private static bool WithinWindow(DateTimeOffset earlier, DateTimeOffset later)
    {
        var delta = later - earlier;
        return delta >= TimeSpan.Zero && delta <= NotificationCorrelationWindow;
    }
}
