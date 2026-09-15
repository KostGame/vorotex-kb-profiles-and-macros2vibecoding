namespace Vorotex.K15.StatusLab;

internal enum CodexPetVisualState { Idle, Running, Waiting, Review }

internal sealed record CodexPetVisualSnapshot(
    CodexPetVisualState State,
    string Reason,
    string? SessionId,
    string? ThreadId);

internal sealed record CodexPetTaskRow(
    string SessionId,
    string? ThreadId,
    CodexPetVisualState VisualState,
    string DisplayTitle,
    string? DisplaySubtitle,
    DateTimeOffset? LastActivityUtc);

internal sealed record CodexPetPresentation(
    CodexPetVisualSnapshot Global,
    IReadOnlyList<CodexPetTaskRow> Tasks)
{
    internal int RelevantTaskCount => Tasks.Count;
}

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

    internal static CodexPetPresentation MapPresentation(
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
        return MapPresentation(sessions, unreadByThread);
    }

    internal static CodexPetPresentation MapPresentation(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        IReadOnlyDictionary<string, CodexUnreadState> unreadByThread)
    {
        var tasks = sessions
            .Where(session => (session.IsAlive && (session.State is K15NormalizedState.Waiting or K15NormalizedState.Running)) ||
                session.State == K15NormalizedState.DonePendingAttention)
            .Select(session => ToTaskRow(session, unreadByThread))
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderBy(row => Priority(row.VisualState))
            .ThenByDescending(row => row.LastActivityUtc ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.SessionId, StringComparer.Ordinal)
            .ToArray();

        var global = tasks.Any(row => row.VisualState == CodexPetVisualState.Waiting)
            ? AggregateForPresentation(tasks, CodexPetVisualState.Waiting, "aggregate_waiting")
            : tasks.Any(row => row.VisualState == CodexPetVisualState.Review)
                ? AggregateForPresentation(tasks, CodexPetVisualState.Review, "aggregate_exact_unread_review")
                : tasks.Any(row => row.VisualState == CodexPetVisualState.Running)
                    ? AggregateForPresentation(tasks, CodexPetVisualState.Running, "aggregate_running")
                    : new(CodexPetVisualState.Idle, "aggregate_idle", null, null);
        return new(global, tasks);
    }

    private static CodexPetTaskRow? ToTaskRow(
        CodexSessionSnapshot session, IReadOnlyDictionary<string, CodexUnreadState> unreadByThread)
    {
        var visualState = session.State switch
        {
            K15NormalizedState.Waiting when session.IsAlive => CodexPetVisualState.Waiting,
            K15NormalizedState.Running when session.IsAlive => CodexPetVisualState.Running,
            K15NormalizedState.DonePendingAttention when !string.IsNullOrWhiteSpace(session.ThreadId) &&
                unreadByThread.TryGetValue(session.ThreadId, out var unread) && unread == CodexUnreadState.HasUnread =>
                CodexPetVisualState.Review,
            _ => (CodexPetVisualState?)null
        };
        if (visualState is null) return null;
        var title = DeriveDisplayTitle(session.Cwd);
        var subtitle = visualState == CodexPetVisualState.Review && !string.IsNullOrWhiteSpace(session.ThreadId)
            ? "Review · " + ShortId(session.ThreadId)
            : visualState == CodexPetVisualState.Waiting ? "Waiting" : "Running";
        return new(session.SessionId, string.IsNullOrWhiteSpace(session.ThreadId) ? null : session.ThreadId,
            visualState.Value, title, subtitle, session.LastActivityUtc);
    }

    internal static string DeriveDisplayTitle(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "Codex task";
        try
        {
            var trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? "Codex task" : name;
        }
        catch (ArgumentException) { return "Codex task"; }
    }

    internal static string FormatTaskCount(int count) => count > 99 ? "99+" : count.ToString();

    private static int Priority(CodexPetVisualState state) => state switch
    {
        CodexPetVisualState.Waiting => 0,
        CodexPetVisualState.Review => 1,
        CodexPetVisualState.Running => 2,
        _ => 3
    };

    private static CodexPetVisualSnapshot AggregateForPresentation(
        IReadOnlyList<CodexPetTaskRow> tasks, CodexPetVisualState state, string reason)
    {
        var matching = tasks.Where(task => task.VisualState == state).ToArray();
        var single = matching.Length == 1 ? matching[0] : null;
        return new(state, reason, single?.SessionId, single?.ThreadId);
    }

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];

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
