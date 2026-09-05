using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal static class EventJournal
{
    private const string MutexName = @"Local\VorotexK15StatusLabJournal";
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const int MaxArchives = 2;
    private static string? _testDirectoryPath;

    public static string DirectoryPath => _testDirectoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VOROTEX",
            "K15 Status Lab");

    public static string FilePath => Path.Combine(DirectoryPath, "events.jsonl");
    public static string DetailedLoggingMarkerPath => Path.Combine(DirectoryPath, "detailed-logging.disabled");
    public static bool DetailedLoggingEnabled => !File.Exists(DetailedLoggingMarkerPath);

    // Narrow same-assembly test seam; production callers never set it.
    internal static void SetTestDirectoryPath(string? path) => _testDirectoryPath = path;

    public static void SetDetailedLoggingEnabled(bool enabled)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (enabled)
        {
            if (File.Exists(DetailedLoggingMarkerPath))
                File.Delete(DetailedLoggingMarkerPath);
        }
        else
        {
            File.WriteAllText(DetailedLoggingMarkerPath, "disabled", new UTF8Encoding(false));
        }
    }

    public static void Append(object record)
    {
        Directory.CreateDirectory(DirectoryPath);
        var line = JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (!DetailedLoggingEnabled && !IsOperationalRecord(line))
            return;

        using var mutex = new Mutex(false, MutexName);
        var locked = false;
        try
        {
            locked = mutex.WaitOne(TimeSpan.FromSeconds(5));
            if (!locked)
                throw new TimeoutException("Timed out waiting for the Status Lab journal lock.");

            RotateIfNeededLocked();
            File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        finally
        {
            if (locked)
                mutex.ReleaseMutex();
        }
    }

    public static void EnsureExists()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(FilePath))
            File.WriteAllText(FilePath, string.Empty, new UTF8Encoding(false));
    }

    public static void Clear()
    {
        Directory.CreateDirectory(DirectoryPath);
        using var mutex = new Mutex(false, MutexName);
        var locked = false;
        try
        {
            locked = mutex.WaitOne(TimeSpan.FromSeconds(5));
            if (!locked)
                throw new TimeoutException("Timed out waiting for the Status Lab journal lock.");

            File.WriteAllText(FilePath, string.Empty, new UTF8Encoding(false));
            for (var index = 1; index <= MaxArchives; index++)
            {
                var archive = ArchivePath(index);
                if (File.Exists(archive))
                    File.Delete(archive);
            }
        }
        finally
        {
            if (locked)
                mutex.ReleaseMutex();
        }
    }

    public static string ArchivePath(int index) => FilePath + "." + index;

    private static bool IsOperationalRecord(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("source", out var sourceNode) || sourceNode.ValueKind != JsonValueKind.String)
                return false;

            var source = sourceNode.GetString() ?? string.Empty;
            if (source == "codex_hook")
                return true;

            if (source == "codex_stdio_bridge")
                return IsSanitizedApprovalRecord(root);

            if (source == "state_normalizer")
                return IsSafeNormalizerRecord(root);

            if (source == "live_dashboard")
                return IsSafeDashboardRecord(root);

            if (source != "windows_notification")
                return false;

            if (!root.TryGetProperty("packageFamilyName", out var packageNode) || packageNode.ValueKind != JsonValueKind.String)
                return false;

            return (packageNode.GetString() ?? string.Empty)
                .StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSafeNormalizerRecord(JsonElement root)
    {
        var name = GetString(root, "event");
        if (name == "read_ack_evidence") return IsSafeReadAckEvidence(root);
        if (name is not ("normalized_state_changed" or "session_state_changed" or "state_rehydrated" or "normalizer_error"))
            return false;
        var allowed = name switch
        {
            "normalized_state_changed" => new[] { "timestampUtc", "source", "event", "plane", "previous", "current", "reason", "sourceTimestampUtc", "focusedSessionId", "focusedCwd", "activeTaskSessions", "attention", "aggregatePrevious", "aggregateCurrent", "driverSessionId", "driverReason", "runningCount", "waitingCount", "doneUnreadCount" },
            "session_state_changed" => new[] { "timestampUtc", "source", "event", "plane", "sessionId", "previous", "current", "reason", "sourceTimestampUtc", "isRehydrated", "correlation" },
            "state_rehydrated" => new[] { "timestampUtc", "source", "event", "current", "focusedSessionId", "focusedCwd", "activeTaskSessions", "attention", "replayWindowMinutes" },
            _ => new[] { "timestampUtc", "source", "event", "exception", "hresult" }
        };
        if (!root.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal)) ||
            !DateTimeOffset.TryParse(GetString(root, "timestampUtc"), out _))
            return false;

        foreach (var nameToCheck in new[] { "previous", "current", "aggregatePrevious", "aggregateCurrent", "state" })
        {
            if (root.TryGetProperty(nameToCheck, out var state) &&
                (state.ValueKind != JsonValueKind.String || !IsSafeState(state.GetString()))) return false;
        }
        if (root.TryGetProperty("reason", out var reason) &&
            (reason.ValueKind != JsonValueKind.String || !IsSafeReason(reason.GetString()))) return false;
        if (root.TryGetProperty("driverReason", out var driverReason) &&
            (driverReason.ValueKind != JsonValueKind.String || !IsSafeReason(driverReason.GetString()))) return false;
        if (root.TryGetProperty("correlation", out var correlation) && !IsSafeCorrelation(correlation)) return false;
        if (root.TryGetProperty("attention", out var attention) && !IsSafeAttention(attention)) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "timestampUtc" or "sourceTimestampUtc" or "correlation" or "attention") continue;
            if (property.Value.ValueKind == JsonValueKind.String && Encoding.UTF8.GetByteCount(property.Value.GetString() ?? string.Empty) > 256) return false;
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return false;
        }
        return true;
    }

    private static bool IsSafeState(string? value) => value is "NORMAL" or "RUNNING" or "WAITING" or "DONE_PENDING_ATTENTION" or "ERROR" or "ENDED";
    private static bool IsSafeReason(string? value) => value is "codex_read_ack" or "codex_user_prompt_submit" or "codex_permission_request" or "codex_pre_tool_use" or "codex_post_tool_use" or "codex_stop" or "codex_session_end" or "codex_approval_resolved" or "codex_turn_completed" or "state_rehydrated" or "stale_attention_timeout" or "aggregate_precedence_normal" or "aggregate_precedence_running" or "aggregate_precedence_waiting" or "aggregate_precedence_donependingattention";
    private static bool IsSafeReadAckEvidence(JsonElement root)
    {
        var allowed = new[] { "timestampUtc", "source", "event", "reason", "host", "sessionId", "threadId", "turnId", "runtimeEpoch", "completionGeneration", "completedUtc", "hasUnreadUtc", "firstNoUnreadUtc", "secondNoUnreadUtc" };
        if (root.EnumerateObject().Count() != allowed.Length ||
            root.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)) ||
            root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != allowed.Length ||
            GetString(root, "host") != "local" || GetString(root, "reason") != "codex_read_ack" ||
            !Guid.TryParse(GetString(root, "runtimeEpoch"), out _) ||
            !root.TryGetProperty("completionGeneration", out var generation) || generation.ValueKind != JsonValueKind.Number ||
            !generation.TryGetInt64(out var number) || number < 1)
            return false;
        foreach (var name in new[] { "sessionId", "threadId", "turnId" })
        {
            var value = GetString(root, name);
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 128) return false;
        }
        foreach (var name in new[] { "timestampUtc", "completedUtc", "hasUnreadUtc", "firstNoUnreadUtc", "secondNoUnreadUtc" })
            if (!DateTimeOffset.TryParse(GetString(root, name), out _)) return false;
        return true;
    }
    private static bool IsSafeCorrelation(JsonElement value)
    {
        var allowed = new[] { "threadId", "turnId", "rpcIdType", "rpcId" };
        return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal) && p.Value.ValueKind == JsonValueKind.String && Encoding.UTF8.GetByteCount(p.Value.GetString() ?? string.Empty) <= 128);
    }
    private static bool IsSafeAttention(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var allowed = new[] { "runningCount", "approvalWaitingCount", "doneUnreadCount", "activeTaskSessionCount", "endedSessionCount", "aggregateState", "noRunningSinceUtc", "staleResetDueUtc", "driverSessionId", "driverReason" };
        if (!value.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal))) return false;
        foreach (var p in value.EnumerateObject())
        {
            if (p.Name is "runningCount" or "approvalWaitingCount" or "doneUnreadCount" or "activeTaskSessionCount" or "endedSessionCount")
            { if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var n) || n < 0 || n > 100000) return false; }
            else if (p.Name == "aggregateState") { if (p.Value.ValueKind != JsonValueKind.String || !IsSafeState(p.Value.GetString())) return false; }
            else if (p.Name is "noRunningSinceUtc" or "staleResetDueUtc") { if (p.Value.ValueKind == JsonValueKind.Null) continue; if (p.Value.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(p.Value.GetString(), out _)) return false; }
            else if (p.Name == "driverSessionId") { if (p.Value.ValueKind == JsonValueKind.Null) continue; if (p.Value.ValueKind != JsonValueKind.String || Encoding.UTF8.GetByteCount(p.Value.GetString() ?? string.Empty) > 128) return false; }
            else if (p.Name == "driverReason") { if (p.Value.ValueKind != JsonValueKind.String || !IsSafeReason(p.Value.GetString())) return false; }
            else return false;
        }
        return true;
    }

    private static bool IsSafeDashboardRecord(JsonElement root)
    {
        var name = GetString(root, "event");
        if (name is not ("started" or "stopped" or "bind_failed" or "runtime_error")) return false;
        var allowed = name == "started"
            ? new[] { "timestampUtc", "source", "event", "version", "loopbackPort" }
            : new[] { "timestampUtc", "source", "event", "exceptionType", "hresult", "category" };
        return root.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal)) &&
               DateTimeOffset.TryParse(GetString(root, "timestampUtc"), out _);
    }

    private static void RotateIfNeededLocked()
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length < MaxFileBytes)
            return;

        for (var index = MaxArchives; index >= 1; index--)
        {
            var destination = ArchivePath(index);
            if (File.Exists(destination))
                File.Delete(destination);

            var source = index == 1 ? FilePath : ArchivePath(index - 1);
            if (File.Exists(source))
                File.Move(source, destination);
        }

        File.WriteAllText(FilePath, string.Empty, new UTF8Encoding(false));
    }

    private static bool IsSanitizedApprovalRecord(JsonElement root)
    {
        var schemaVersion = GetString(root, "schemaVersion");
        if (schemaVersion == "k15-codex-completion/v1")
        {
            var completionAllowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "schemaVersion", "timestampUtc", "source", "event", "threadId", "turnId", "status"
            };
            if (root.EnumerateObject().Any(property => !completionAllowed.Contains(property.Name)))
                return false;

            if (GetString(root, "event") != "turn_completed" ||
                GetString(root, "status") is not ("completed" or "interrupted" or "failed") ||
                string.IsNullOrWhiteSpace(GetString(root, "threadId")) ||
                string.IsNullOrWhiteSpace(GetString(root, "turnId")) ||
                !DateTimeOffset.TryParse(GetString(root, "timestampUtc"), out _))
            {
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String ||
                    Encoding.UTF8.GetByteCount(property.Value.GetString() ?? string.Empty) > 1024)
                {
                    return false;
                }
            }

            return true;
        }

        if (schemaVersion != "k15-codex-approval/v1")
            return false;

        var approvalAllowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "timestampUtc", "source", "event", "decision",
            "rpcIdType", "rpcId", "threadId", "turnId", "itemId"
        };
        if (root.EnumerateObject().Any(property => !approvalAllowed.Contains(property.Name)))
            return false;

        if (GetString(root, "event") != "approval_resolved" ||
            GetString(root, "decision") is not ("accept" or "acceptForSession" or "decline" or "cancel") ||
            GetString(root, "rpcIdType") is not ("number" or "string") ||
            string.IsNullOrWhiteSpace(GetString(root, "rpcId")) ||
            !DateTimeOffset.TryParse(GetString(root, "timestampUtc"), out _))
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String ||
                Encoding.UTF8.GetByteCount(property.Value.GetString() ?? string.Empty) > 1024)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;
    }
}
