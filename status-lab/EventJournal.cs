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
        if (name is not ("normalized_state_changed" or "session_state_changed" or "state_rehydrated" or "normalizer_error"))
            return false;
        var allowed = name switch
        {
            "normalized_state_changed" => new[] { "timestampUtc", "source", "event", "plane", "previous", "current", "reason", "sourceTimestampUtc", "focusedSessionId", "focusedCwd", "activeTaskSessions", "aggregatePrevious", "aggregateCurrent", "driverSessionId", "driverReason", "runningCount", "waitingCount", "doneUnreadCount" },
            "session_state_changed" => new[] { "timestampUtc", "source", "event", "plane", "sessionId", "previous", "current", "reason", "sourceTimestampUtc", "isRehydrated", "correlation" },
            "state_rehydrated" => new[] { "timestampUtc", "source", "event", "current", "focusedSessionId", "focusedCwd", "activeTaskSessions", "attention", "replayWindowMinutes" },
            _ => new[] { "timestampUtc", "source", "event", "exception", "hresult" }
        };
        return root.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal)) &&
               DateTimeOffset.TryParse(GetString(root, "timestampUtc"), out _);
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
