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
    internal static CodexPetVisualSnapshot Map(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        string? targetSessionId,
        Func<string, CodexUnreadState> unreadForThread)
    {
        var relevant = sessions.Where(session => session.IsAlive ||
            session.State == K15NormalizedState.DonePendingAttention).ToArray();
        if (relevant.Length == 0)
            return new(CodexPetVisualState.Idle, "no_relevant_session", null, null);
        if (relevant.Length > 1)
            return new(CodexPetVisualState.Idle, "ambiguous_multiple_sessions", null, null);

        var session = relevant[0];
        return session.State switch
        {
            K15NormalizedState.Running => new(CodexPetVisualState.Running, "running", session.SessionId, session.ThreadId),
            K15NormalizedState.Waiting => new(CodexPetVisualState.Waiting, "waiting", session.SessionId, session.ThreadId),
            K15NormalizedState.DonePendingAttention when !string.IsNullOrWhiteSpace(targetSessionId) &&
                !string.Equals(targetSessionId, session.SessionId, StringComparison.Ordinal) =>
                new(CodexPetVisualState.Idle, "target_session_mismatch", null, null),
            K15NormalizedState.DonePendingAttention when !string.IsNullOrWhiteSpace(session.ThreadId) &&
                unreadForThread(session.ThreadId) == CodexUnreadState.HasUnread =>
                new(CodexPetVisualState.Review, "exact_thread_unread", session.SessionId, session.ThreadId),
            K15NormalizedState.DonePendingAttention when unreadForThread(session.ThreadId) is CodexUnreadState.Unknown or CodexUnreadState.Unavailable =>
                new(CodexPetVisualState.Idle, "unread_unavailable", session.SessionId, session.ThreadId),
            K15NormalizedState.DonePendingAttention => new(CodexPetVisualState.Idle, "no_unread", session.SessionId, session.ThreadId),
            _ => new(CodexPetVisualState.Idle, "normal", session.SessionId, session.ThreadId)
        };
    }
}
