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

    private readonly HashSet<uint> _waitingNotificationIds = new();
    private readonly HashSet<uint> _doneNotificationIds = new();
    private DateTimeOffset? _lastPermissionUtc;
    private DateTimeOffset? _lastStopUtc;

    public K15NormalizedState State { get; private set; } = K15NormalizedState.Normal;

    public StateTransition? Apply(StatusInputEvent input)
    {
        if (input.Source.Equals("codex_hook", StringComparison.Ordinal))
            return ApplyCodex(input);

        if (input.Source.Equals("windows_notification", StringComparison.Ordinal))
            return ApplyNotification(input);

        return null;
    }

    public StateTransition? Acknowledge(DateTimeOffset timestampUtc, string reason = "manual_acknowledge")
    {
        _waitingNotificationIds.Clear();
        _doneNotificationIds.Clear();
        _lastPermissionUtc = null;
        _lastStopUtc = null;
        return SetState(K15NormalizedState.Normal, reason, timestampUtc);
    }

    private StateTransition? ApplyCodex(StatusInputEvent input)
    {
        switch (input.EventName)
        {
            case "UserPromptSubmit":
                _waitingNotificationIds.Clear();
                _doneNotificationIds.Clear();
                _lastPermissionUtc = null;
                _lastStopUtc = null;
                return SetState(K15NormalizedState.Running, "codex_user_prompt_submit", input.TimestampUtc);

            case "PermissionRequest":
                _lastPermissionUtc = input.TimestampUtc;
                return SetState(K15NormalizedState.Waiting, "codex_permission_request", input.TimestampUtc);

            case "Stop":
                _waitingNotificationIds.Clear();
                _lastPermissionUtc = null;
                _lastStopUtc = input.TimestampUtc;
                return SetState(K15NormalizedState.DonePendingAttention, "codex_stop", input.TimestampUtc);

            case "SessionEnd":
                _waitingNotificationIds.Clear();
                _doneNotificationIds.Clear();
                _lastPermissionUtc = null;
                _lastStopUtc = null;
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
                return SetState(K15NormalizedState.Normal, "done_notification_resolved", input.TimestampUtc);
            }
        }

        return null;
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
