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
    DateTimeOffset? LastActivityUtc,
    CodexActivityRow Activity);

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
        ICodexUnreadSourceRegistry sources)
    {
        var unreadByIdentity = new Dictionary<string, CodexUnreadState>(StringComparer.Ordinal);
        foreach (var sourceGroup in sessions
            .Where(session => session.State == K15NormalizedState.DonePendingAttention &&
                              !string.IsNullOrWhiteSpace(session.ThreadId))
            .GroupBy(session => session.SourceInstanceId, StringComparer.Ordinal))
        {
            var snapshot = sources.Read(sourceGroup.Key, DateTimeOffset.UtcNow);
            foreach (var session in sourceGroup)
                unreadByIdentity[CodexSourceIdentity.CompositeKey(sourceGroup.Key, session.ThreadId!)] =
                    snapshot.ForThread(session.ThreadId!);
        }
        return MapPresentation(sessions, unreadByIdentity);
    }

    internal static CodexPetPresentation MapPresentation(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        IReadOnlyDictionary<string, CodexUnreadState> unreadByThread)
    {
        var activityRows = CodexActivityNormalizer.Normalize(sessions, unreadByThread);
        var tasks = activityRows
            .Select(ToTaskRow)
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderBy(row => Priority(row.VisualState))
            .ThenByDescending(row => row.LastActivityUtc ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.SessionId, StringComparer.Ordinal)
            .ToArray();

        // Keep aggregate precedence and identity semantics in the established
        // compatibility mapper. Task rows are presentation-only.
        var global = Map(sessions, unreadByThread);
        return new(global, tasks);
    }

    internal static CodexPetPresentation MapPresentation(
        IReadOnlyList<CodexSessionSnapshot> localSessions,
        IReadOnlyDictionary<string, CodexUnreadState> localUnreadByThread,
        CodexRemoteActivitySnapshot remote)
    {
        var activityRows = CodexActivityNormalizer.Normalize(localSessions, localUnreadByThread, remote);
        var tasks = activityRows
            .Select(ToTaskRow)
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderBy(row => Priority(row.VisualState))
            .ThenByDescending(row => row.LastActivityUtc ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.SessionId, StringComparer.Ordinal)
            .ToArray();
        return new(MapUnified(activityRows), tasks);
    }

    private static CodexPetTaskRow? ToTaskRow(CodexActivityRow activity)
    {
        var visualState = activity.State switch
        {
            CodexActivityState.Waiting => CodexPetVisualState.Waiting,
            CodexActivityState.Running => CodexPetVisualState.Running,
            CodexActivityState.DonePendingAttention when activity.Unread == CodexUnreadState.HasUnread =>
                CodexPetVisualState.Review,
            _ => (CodexPetVisualState?)null
        };
        if (visualState is null) return null;
        var context = activity.Project ?? activity.Repository;
        if (activity.Project is not null && activity.Repository is not null)
            context = activity.Project + " · " + activity.Repository;
        var subtitle = visualState == CodexPetVisualState.Review && !string.IsNullOrWhiteSpace(activity.ThreadId)
            ? "Review · " + ShortId(activity.ThreadId)
            : visualState == CodexPetVisualState.Waiting ? "Waiting" : "Running";
        if (context is not null) subtitle += " · " + context;
        return new(activity.SessionId, activity.ThreadId, visualState.Value, activity.Title, subtitle,
            activity.ObservedAt, activity);
    }

    private static CodexPetVisualSnapshot MapUnified(IReadOnlyList<CodexActivityRow> rows)
    {
        if (rows.Any(row => row.State == CodexActivityState.Waiting))
            return Aggregate(rows, CodexPetVisualState.Waiting, "aggregate_waiting");
        if (rows.Any(row => row.State == CodexActivityState.DonePendingAttention &&
                            row.Unread == CodexUnreadState.HasUnread))
            return Aggregate(rows, CodexPetVisualState.Review, "aggregate_exact_unread_review");
        if (rows.Any(row => row.State == CodexActivityState.Running))
            return Aggregate(rows, CodexPetVisualState.Running, "aggregate_running");
        return Aggregate(rows, CodexPetVisualState.Idle, "aggregate_idle");
    }

    private static CodexPetVisualSnapshot Aggregate(
        IReadOnlyList<CodexActivityRow> rows, CodexPetVisualState state, string reason)
    {
        var single = rows.Count == 1 ? rows[0] : null;
        return new(state, reason, single?.SessionId, single?.ThreadId);
    }

    internal static string FormatTaskCount(int count) => count > 99 ? "99+" : count.ToString();

    private static int Priority(CodexPetVisualState state) => state switch
    {
        CodexPetVisualState.Waiting => 0,
        CodexPetVisualState.Review => 1,
        CodexPetVisualState.Running => 2,
        _ => 3
    };

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
            TryGetUnread(unreadByThread, session.SourceInstanceId, session.ThreadId!, out var unread) &&
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

    private static bool TryGetUnread(IReadOnlyDictionary<string, CodexUnreadState> unreadByThread,
        string sourceInstanceId, string threadId, out CodexUnreadState unread)
    {
        if (unreadByThread.TryGetValue(CodexSourceIdentity.CompositeKey(sourceInstanceId, threadId), out unread))
            return true;
        return string.IsNullOrWhiteSpace(sourceInstanceId) && unreadByThread.TryGetValue(threadId, out unread);
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
