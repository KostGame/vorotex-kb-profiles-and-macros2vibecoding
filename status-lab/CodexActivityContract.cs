using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal enum CodexActivitySource
{
    Local,
    Remote
}

internal enum CodexActivityState
{
    Normal,
    Running,
    Waiting,
    DonePendingAttention,
    Failed,
    Unknown
}

internal enum CodexActivityConfidence
{
    Trusted,
    Degraded,
    Unknown
}

internal enum CodexActivitySourceHealth
{
    Up,
    Down,
    Degraded,
    Pending,
    Contention,
    Unknown
}

internal sealed record CodexActivityOpenTarget(
    CodexActivitySource Source,
    string? ThreadId,
    string? SessionId,
    string? DeepLink,
    bool CanFocus,
    string? UnavailableReason)
{
    internal static CodexActivityOpenTarget Unavailable(
        CodexActivitySource source,
        string? threadId,
        string? sessionId,
        string reason) => new(source, threadId, sessionId, null, false, reason);
}

internal sealed record CodexActivityEvidence(
    CodexActivitySource Source,
    string SessionId,
    string? TaskId,
    CodexActivityState State,
    string EventKind,
    DateTimeOffset ObservedAt,
    long? Sequence,
    CodexActivityConfidence Confidence,
    string ReasonCode);

internal sealed record CodexActivityRow(
    CodexActivitySource Source,
    string ExecutionHost,
    string SessionId,
    string? ThreadId,
    string? TaskId,
    string Title,
    string? Project,
    string? Repository,
    string? Cwd,
    CodexActivityState State,
    CodexUnreadState Unread,
    DateTimeOffset ObservedAt,
    CodexActivityEvidence Evidence,
    CodexActivityConfidence Confidence,
    CodexActivityOpenTarget OpenTarget,
    string SourceInstanceId = "")
{
    internal string IdentityKey => string.Join("/", Source, ExecutionHost,
        SourceInstanceId, ThreadId ?? string.Empty, TaskId ?? string.Empty, SessionId);
}

internal sealed record CodexActivitySourceStatus(
    CodexActivitySource Source,
    CodexActivitySourceHealth Status,
    string Reason,
    DateTimeOffset ObservedAt,
    string Evidence);

// This is the bounded, already-structured input expected from Codex App's
// thread index. It is not a JSON-RPC envelope and carries no prompt/content.
internal sealed record CodexRemoteThreadObservation(
    string ExecutionHost,
    string ThreadId,
    string Status,
    DateTimeOffset ObservedAt,
    string? Title = null,
    string? Project = null,
    string? Repository = null,
    string? Cwd = null,
    string? SessionId = null,
    string? TaskId = null,
    CodexUnreadState Unread = CodexUnreadState.Unknown,
    IReadOnlySet<string>? ActiveFlags = null,
    long? Sequence = null);

internal sealed record CodexRemoteActivitySnapshot(
    IReadOnlyList<CodexRemoteThreadObservation> Threads,
    CodexActivitySourceStatus Status);

internal static class CodexActivityNormalizer
{
    internal const string RemoteSourceEvidence = "codex_app_thread_index/v1";
    internal const string ExactFocusUnavailable = "exact_codex_client_focus_not_proven";
    internal const string ThreadIdUnavailable = "thread_id_unavailable";

    internal static IReadOnlyList<CodexActivityRow> Normalize(
        IReadOnlyList<CodexSessionSnapshot> localSessions,
        IReadOnlyDictionary<string, CodexUnreadState> localUnreadByThread,
        CodexRemoteActivitySnapshot? remote = null)
    {
        var rows = new List<CodexActivityRow>();
        foreach (var session in localSessions)
        {
            var row = Local(session, localUnreadByThread);
            if (row is not null) rows.Add(row);
        }

        // Only a healthy structured source can authorize trusted remote task
        // lifecycle. Any degraded, pending, contention, unknown, or down
        // snapshot is a source failure, not task evidence.
        if (remote is not null && remote.Status.Status == CodexActivitySourceHealth.Up)
        {
            foreach (var thread in remote.Threads)
            {
                var row = Remote(thread);
                if (row is not null) rows.Add(row);
            }
        }

        return rows
            .GroupBy(row => row.IdentityKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(row => row.Confidence == CodexActivityConfidence.Trusted)
                .ThenByDescending(row => row.ObservedAt)
                .First())
            .OrderBy(row => row.Source)
            .ThenBy(row => row.ExecutionHost, StringComparer.Ordinal)
            .ThenByDescending(row => row.ObservedAt)
            .ThenBy(row => row.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static CodexActivityRow? Local(
        CodexSessionSnapshot session,
        IReadOnlyDictionary<string, CodexUnreadState> unreadByThread)
    {
        if (!Bounded(session.SessionId, 256)) return null;
        var threadId = Bounded(session.ThreadId, 256) ? session.ThreadId : null;
        var state = session.State switch
        {
            K15NormalizedState.Normal => CodexActivityState.Normal,
            K15NormalizedState.Running when session.IsAlive => CodexActivityState.Running,
            K15NormalizedState.Waiting when session.IsAlive => CodexActivityState.Waiting,
            K15NormalizedState.DonePendingAttention => CodexActivityState.DonePendingAttention,
            K15NormalizedState.Error => CodexActivityState.Failed,
            _ => CodexActivityState.Unknown
        };
        var unread = state == CodexActivityState.DonePendingAttention && threadId is not null &&
                     TryGetUnread(unreadByThread, session.SourceInstanceId, threadId, out var value)
            ? value
            : CodexUnreadState.Unavailable;
        var evidence = new CodexActivityEvidence(CodexActivitySource.Local, session.SessionId, null,
            state, "local_reducer_snapshot", session.LastActivityUtc ?? DateTimeOffset.UtcNow,
            null, CodexActivityConfidence.Trusted, "local_reducer_state");
        return new(CodexActivitySource.Local, "local", session.SessionId, threadId, null,
            FallbackTitle(session.SessionId), null, null, Bounded(session.Cwd, 1024) ? session.Cwd : null,
            state, unread, session.LastActivityUtc ?? DateTimeOffset.UtcNow, evidence,
            CodexActivityConfidence.Trusted,
            threadId is null
                ? CodexActivityOpenTarget.Unavailable(CodexActivitySource.Local, null, session.SessionId,
                    ThreadIdUnavailable)
                : CodexActivityOpenTarget.Unavailable(CodexActivitySource.Local, threadId, session.SessionId,
                    ExactFocusUnavailable),
            session.SourceInstanceId);
    }

    private static bool TryGetUnread(IReadOnlyDictionary<string, CodexUnreadState> unreadByThread,
        string sourceInstanceId, string threadId, out CodexUnreadState value)
    {
        if (unreadByThread.TryGetValue(CodexSourceIdentity.CompositeKey(sourceInstanceId, threadId), out value))
            return true;
        return string.IsNullOrWhiteSpace(sourceInstanceId) && unreadByThread.TryGetValue(threadId, out value);
    }

    internal static CodexActivityRow? Remote(CodexRemoteThreadObservation observation)
    {
        if (!BoundedRemoteHost(observation.ExecutionHost) || !Bounded(observation.ThreadId, 256) ||
            !TryMapRemoteState(observation.Status, observation.ActiveFlags, out var state)) return null;

        var sessionId = Bounded(observation.SessionId, 256) ? observation.SessionId! : observation.ThreadId;
        // Codex App exposes exact thread identity, but no separate runtime
        // session ID. Thread ID is the stable source key. Do not synthesize a
        // second correlation ID; retain exact thread identity in the target.
        var confidence = CodexActivityConfidence.Trusted;
        var reason = string.IsNullOrWhiteSpace(observation.SessionId)
            ? "remote_thread_identity_only"
            : "remote_structured_thread_status";
        var title = SanitizeLabel(observation.Title) ?? FallbackTitle(observation.ThreadId);
        var project = SanitizeLabel(observation.Project);
        var repository = SanitizeLabel(observation.Repository);
        var cwd = Bounded(observation.Cwd, 1024) ? observation.Cwd : null;
        var evidence = new CodexActivityEvidence(CodexActivitySource.Remote, sessionId,
            Bounded(observation.TaskId, 256) ? observation.TaskId : null, state,
            RemoteSourceEvidence, observation.ObservedAt, observation.Sequence, confidence, reason);
        return new(CodexActivitySource.Remote, observation.ExecutionHost, sessionId, observation.ThreadId,
            evidence.TaskId, title, project, repository, cwd, state, observation.Unread,
            observation.ObservedAt, evidence, confidence,
            CodexActivityOpenTarget.Unavailable(CodexActivitySource.Remote, observation.ThreadId,
                Bounded(observation.SessionId, 256) ? observation.SessionId : null, ExactFocusUnavailable));
    }

    internal static string FallbackTitle(string stableId)
    {
        var shortId = Bounded(stableId, 256) ? stableId.Trim() : "unknown";
        if (shortId.Length > 8) shortId = shortId[..8];
        return "Codex task " + shortId;
    }

    internal static string? SanitizeLabel(string? value)
    {
        if (!Bounded(value, 256)) return null;
        var label = value!.Trim();
        return label.Length == 0 ? null : label;
    }

    internal static IReadOnlyList<CodexActivityRow> EnrichLocalTitles(
        IReadOnlyList<CodexActivityRow> rows,
        IReadOnlyDictionary<string, string> namesBySourceAndThread)
    {
        return rows.Select(row =>
        {
            if (row.Source != CodexActivitySource.Local)
                return row;
            if (!CodexSourceIdentity.IsValid(row.SourceInstanceId) || row.ThreadId is null ||
                !namesBySourceAndThread.TryGetValue(
                    CodexSourceIdentity.CompositeKey(row.SourceInstanceId, row.ThreadId), out var candidate))
                return row with { Title = FallbackTitle(row.SessionId) };

            var name = SanitizeLabel(candidate);
            return row with { Title = name ?? FallbackTitle(row.SessionId) };
        }).ToArray();
    }

    private static bool TryMapRemoteState(string status, IReadOnlySet<string>? flags,
        out CodexActivityState state)
    {
        if (status == "active" && flags is not null &&
            (flags.Contains("waitingOnApproval") || flags.Contains("waitingOnUserInput")))
        {
            state = CodexActivityState.Waiting;
            return true;
        }

        state = status switch
        {
            "active" => CodexActivityState.Running,
            "idle" => CodexActivityState.Normal,
            "notLoaded" => CodexActivityState.Unknown,
            "systemError" => CodexActivityState.Failed,
            _ => CodexActivityState.Unknown
        };
        return status is "active" or "idle" or "notLoaded" or "systemError";
    }

    private static bool BoundedRemoteHost(string? value) =>
        Bounded(value, 256) && value!.StartsWith("remote-ssh-discovered:", StringComparison.Ordinal);

    private static bool Bounded(string? value, int maxBytes) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(value) <= maxBytes;
}

internal static class CodexAppThreadIndexParser
{
    private const string SchemaVersion = "codex-app-thread-index/v1";
    private const int MaxBytes = 256 * 1024;
    private const int MaxThreads = 256;
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    { "schemaVersion", "source", "threads" };
    private static readonly HashSet<string> ThreadProperties = new(StringComparer.Ordinal)
    { "id", "hostId", "status", "updatedAt", "title", "projectId", "cwd", "summary", "kind" };

    internal static CodexRemoteActivitySnapshot Parse(string json, DateTimeOffset observedAt)
    {
        var degraded = new CodexActivitySourceStatus(CodexActivitySource.Remote,
            CodexActivitySourceHealth.Degraded, "invalid_structured_thread_index", observedAt,
            CodexActivityNormalizer.RemoteSourceEvidence);
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            return new(Array.Empty<CodexRemoteThreadObservation>(), degraded);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(property => !RootProperties.Contains(property.Name)) ||
                !String(root, "schemaVersion", SchemaVersion, out _) ||
                !String(root, "source", "codex_app_thread_index", out _) ||
                !root.TryGetProperty("threads", out var threads) ||
                threads.ValueKind != JsonValueKind.Array)
                return new(Array.Empty<CodexRemoteThreadObservation>(), degraded);

            var result = new List<CodexRemoteThreadObservation>();
            foreach (var item in threads.EnumerateArray())
            {
                if (result.Count >= MaxThreads || item.ValueKind != JsonValueKind.Object ||
                    item.EnumerateObject().Any(property => !ThreadProperties.Contains(property.Name)))
                    return new([], degraded);
                if (!String(item, "id", null, out var threadId) ||
                    !String(item, "hostId", null, out var hostId) ||
                    !String(item, "status", null, out var status) ||
                    !item.TryGetProperty("updatedAt", out var updatedAt) ||
                    updatedAt.ValueKind != JsonValueKind.Number || !updatedAt.TryGetInt64(out var unixSeconds) ||
                    !DateTimeOffset.TryParse(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("O"), out var timestamp) ||
                    !IsAllowedStatus(status) ||
                    !hostId!.StartsWith("remote-ssh-discovered:", StringComparison.Ordinal))
                    return new([], degraded);

                var title = OptionalString(item, "title");
                var cwd = OptionalString(item, "cwd");
                var projectId = OptionalString(item, "projectId");
                result.Add(new(hostId!, threadId!, status!, timestamp,
                    title, projectId, null, cwd));
            }

            return new(result, new(CodexActivitySource.Remote, CodexActivitySourceHealth.Up,
                "structured_thread_index", observedAt, CodexActivityNormalizer.RemoteSourceEvidence));
        }
        catch (JsonException)
        {
            return new(Array.Empty<CodexRemoteThreadObservation>(), degraded);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new(Array.Empty<CodexRemoteThreadObservation>(), degraded);
        }
    }

    private static bool IsAllowedStatus(string? value) =>
        value is "active" or "idle" or "notLoaded" or "systemError";

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool String(JsonElement root, string name, string? expected, out string? value)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            value = null;
            return false;
        }
        value = element.GetString();
        return value is not null && (expected is null || value == expected);
    }
}
