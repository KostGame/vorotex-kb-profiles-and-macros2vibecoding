namespace Vorotex.K15.StatusLab;

internal enum CodexPetVisualState { Idle, Running, Waiting, Review }

internal sealed record CodexPetVisualSnapshot(
    CodexPetVisualState State,
    string Reason,
    string? SessionId,
    string? ThreadId);

// Pure presentation mapping. The reducer remains the only source of session
// state; unread evidence is supplied by the caller and is never inferred.
internal static class CodexPetAdapter
{
    internal static bool ShouldPollUnread(IReadOnlyList<CodexSessionSnapshot> sessions)
    {
        var relevant = sessions.Where(session => session.IsAlive ||
            session.State == K15NormalizedState.DonePendingAttention).ToArray();
        return relevant.Any(session => session.State == K15NormalizedState.DonePendingAttention &&
            !string.IsNullOrWhiteSpace(session.ThreadId));
    }

    internal static CodexPetVisualSnapshot Map(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        CodexUnreadSnapshot? unreadSnapshot)
    {
        var unreadByThread = sessions
            .Where(session => session.State == K15NormalizedState.DonePendingAttention &&
                !string.IsNullOrWhiteSpace(session.ThreadId))
            .Select(session => session.ThreadId!)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(threadId => threadId,
                threadId => unreadSnapshot?.ForThread(threadId) ?? CodexUnreadState.Unknown,
                StringComparer.Ordinal);
        return Map(sessions, unreadByThread);
    }

    internal static CodexPetVisualSnapshot Map(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        IReadOnlyDictionary<string, CodexUnreadState> unreadByThread)
    {
        var relevant = sessions.Where(session => session.IsAlive ||
            session.State == K15NormalizedState.DonePendingAttention).ToArray();
        if (relevant.Any(session => session.IsAlive && session.State == K15NormalizedState.Waiting))
            return Aggregate(relevant, CodexPetVisualState.Waiting, "aggregate_waiting");

        var hasUnread = relevant.Any(session =>
            session.State == K15NormalizedState.DonePendingAttention &&
            !string.IsNullOrWhiteSpace(session.ThreadId) &&
            unreadByThread.TryGetValue(session.ThreadId!, out var unread) &&
            unread == CodexUnreadState.HasUnread);
        if (hasUnread)
            return Aggregate(relevant, CodexPetVisualState.Review, "aggregate_exact_unread_review");

        if (relevant.Any(session => session.IsAlive && session.State == K15NormalizedState.Running))
            return Aggregate(relevant, CodexPetVisualState.Running, "aggregate_running");

        return Aggregate(relevant, CodexPetVisualState.Idle, "aggregate_idle");
    }

    private static CodexPetVisualSnapshot Aggregate(
        IReadOnlyList<CodexSessionSnapshot> relevant, CodexPetVisualState state, string reason)
    {
        var single = relevant.Count == 1 ? relevant[0] : null;
        return new(state, reason, single?.SessionId, single?.ThreadId);
    }

    // Compatibility overload for existing single-session callers/tests. Global
    // presentation never uses targetSessionId to choose among multiple chats.
    internal static CodexPetVisualSnapshot Map(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        string? _,
        CodexUnreadState unreadStateForTargetThread)
    {
        var unreadByThread = sessions
            .Where(session => session.State == K15NormalizedState.DonePendingAttention &&
                !string.IsNullOrWhiteSpace(session.ThreadId))
            .Select(session => session.ThreadId!)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(threadId => threadId, _ => unreadStateForTargetThread, StringComparer.Ordinal);
        return Map(sessions, unreadByThread);
    }
}
