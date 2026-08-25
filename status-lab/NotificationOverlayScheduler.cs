namespace Vorotex.K15.StatusLab;

internal enum NotificationOverlayDecisionKind
{
    Show,
    Replace,
    Dismiss
}

internal sealed record ScheduledNotificationOverlay(
    NotificationOverlayIntent Intent,
    DateTimeOffset ActivatedUtc,
    DateTimeOffset ExpiresUtc);

internal sealed record NotificationOverlayDecision(
    NotificationOverlayDecisionKind Kind,
    ScheduledNotificationOverlay? Active,
    string Reason);

internal sealed class NotificationOverlayScheduler
{
    private ScheduledNotificationOverlay? _active;
    private ScheduledNotificationOverlay? _pending;

    public ScheduledNotificationOverlay? Active => _active;
    public ScheduledNotificationOverlay? Pending => _pending;
    public int PendingCount => _pending is null ? 0 : 1;

    public NotificationOverlayDecision? Apply(NotificationOverlayIntent intent, DateTimeOffset nowUtc)
    {
        PruneExpiredPending(nowUtc);

        if (intent.Dismiss)
            return DismissByKey(intent.NotificationKey, nowUtc, "source_removed");

        var incoming = Schedule(intent, nowUtc);
        if (_active is null || IsExpired(_active, nowUtc))
        {
            _active = incoming;
            return new NotificationOverlayDecision(NotificationOverlayDecisionKind.Show, _active, "no_active_overlay");
        }

        if (string.Equals(_active.Intent.NotificationKey, intent.NotificationKey, StringComparison.Ordinal))
        {
            _active = incoming;
            return new NotificationOverlayDecision(NotificationOverlayDecisionKind.Replace, _active, "same_notification_updated");
        }

        if (intent.Priority > _active.Intent.Priority)
        {
            var interrupted = _active;
            _active = incoming;
            _pending = ChoosePending(_pending, interrupted, nowUtc);
            return new NotificationOverlayDecision(NotificationOverlayDecisionKind.Replace, _active, "higher_priority_preempt");
        }

        _pending = ChoosePending(_pending, incoming, nowUtc);
        return null;
    }

    public NotificationOverlayDecision? Tick(DateTimeOffset nowUtc)
    {
        PruneExpiredPending(nowUtc);
        if (_active is null || !IsExpired(_active, nowUtc))
            return null;

        _active = null;
        var promoted = PromotePending(nowUtc);
        return promoted is null
            ? new NotificationOverlayDecision(NotificationOverlayDecisionKind.Dismiss, null, "active_overlay_expired")
            : new NotificationOverlayDecision(NotificationOverlayDecisionKind.Replace, promoted, "active_expired_pending_promoted");
    }

    public NotificationOverlayDecision? Acknowledge(string notificationKey, DateTimeOffset nowUtc) =>
        DismissByKey(notificationKey, nowUtc, "manual_acknowledge");

    public NotificationOverlayDecision? Clear(DateTimeOffset nowUtc, string reason = "manual_clear")
    {
        var hadActive = _active is not null;
        _active = null;
        _pending = null;
        return hadActive
            ? new NotificationOverlayDecision(NotificationOverlayDecisionKind.Dismiss, null, reason)
            : null;
    }

    private NotificationOverlayDecision? DismissByKey(string notificationKey, DateTimeOffset nowUtc, string reason)
    {
        if (_pending is not null &&
            string.Equals(_pending.Intent.NotificationKey, notificationKey, StringComparison.Ordinal))
        {
            _pending = null;
        }

        if (_active is null ||
            !string.Equals(_active.Intent.NotificationKey, notificationKey, StringComparison.Ordinal))
        {
            return null;
        }

        _active = null;
        var promoted = PromotePending(nowUtc);
        return promoted is null
            ? new NotificationOverlayDecision(NotificationOverlayDecisionKind.Dismiss, null, reason)
            : new NotificationOverlayDecision(NotificationOverlayDecisionKind.Replace, promoted, $"{reason}_pending_promoted");
    }

    private ScheduledNotificationOverlay? PromotePending(DateTimeOffset nowUtc)
    {
        PruneExpiredPending(nowUtc);
        if (_pending is null)
            return null;

        _active = _pending;
        _pending = null;
        return _active;
    }

    private void PruneExpiredPending(DateTimeOffset nowUtc)
    {
        if (_pending is not null && IsExpired(_pending, nowUtc))
            _pending = null;
    }

    private static ScheduledNotificationOverlay Schedule(NotificationOverlayIntent intent, DateTimeOffset nowUtc)
    {
        var lifetimeSeconds = intent.Behavior == NotificationBehavior.Pulse
            ? intent.Display.DurationSeconds
            : intent.MaxDurationSeconds;

        if (lifetimeSeconds <= 0)
            lifetimeSeconds = 0.5;

        return new ScheduledNotificationOverlay(
            intent,
            nowUtc,
            nowUtc + TimeSpan.FromSeconds(lifetimeSeconds));
    }

    private static ScheduledNotificationOverlay? ChoosePending(
        ScheduledNotificationOverlay? current,
        ScheduledNotificationOverlay candidate,
        DateTimeOffset nowUtc)
    {
        if (IsExpired(candidate, nowUtc))
            return current is not null && !IsExpired(current, nowUtc) ? current : null;
        if (current is null || IsExpired(current, nowUtc))
            return candidate;

        if (candidate.Intent.Priority > current.Intent.Priority)
            return candidate;
        if (candidate.Intent.Priority < current.Intent.Priority)
            return current;

        return candidate.Intent.SourceCreatedUtc >= current.Intent.SourceCreatedUtc
            ? candidate
            : current;
    }

    private static bool IsExpired(ScheduledNotificationOverlay overlay, DateTimeOffset nowUtc) =>
        nowUtc >= overlay.ExpiresUtc;
}
