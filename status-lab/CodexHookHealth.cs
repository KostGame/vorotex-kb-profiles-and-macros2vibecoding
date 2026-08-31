using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal sealed record CodexHookHealthSnapshot(string Status, int HomesFound, int HealthyHomes, string Detail)
{
    public bool Healthy => HomesFound > 0 && HomesFound == HealthyHomes;
}

internal static class CodexHookHealth
{
    private static readonly string[] RequiredEvents =
    ["UserPromptSubmit", "PermissionRequest", "PreToolUse", "PostToolUse", "Stop", "SessionEnd"];

    private static string StableLoggerPath => Path.GetFullPath(Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VorotexK15", "app", "hooks", "codex-hook-logger.ps1"));

    public static CodexHookHealthSnapshot Inspect()
    {
        return InspectHomes(DetectHomes());
    }

    internal static CodexHookHealthSnapshot InspectHomes(IReadOnlyList<string> homes)
    {
        if (homes.Count == 0) return new("Не установлены", 0, 0, "Codex home не найден");

        var healthy = 0;
        var details = new List<string>();
        foreach (var home in homes)
        {
            var hooksPath = Path.Combine(home, "hooks.json");
            if (!File.Exists(hooksPath)) { details.Add($"{home}: hooks.json отсутствует"); continue; }
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(hooksPath));
                var problems = Validate(document.RootElement);
                if (problems.Count == 0) { healthy++; details.Add($"{home}: OK"); }
                else details.Add($"{home}: обновить ({string.Join(", ", problems)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { details.Add($"{home}: malformed hooks.json"); }
        }

        var status = healthy == homes.Count ? "Установлены · актуальны" :
            healthy == 0 ? "Нужно установить / обновить" : "Частично актуальны";
        return new(status, homes.Count, healthy, string.Join(" · ", details));
    }

    private static List<string> Validate(JsonElement root)
    {
        var problems = new List<string>();
        if (!root.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object)
        { problems.Add("missing hooks object"); return problems; }

        var knownEvents = new HashSet<string>(RequiredEvents, StringComparer.Ordinal);
        foreach (var property in hooks.EnumerateObject())
        {
            if (!knownEvents.Contains(property.Name) && GetStatusLabHandlers(property.Value).Any())
                problems.Add($"stale/unexpected Status Lab event {property.Name}");
        }
        foreach (var eventName in RequiredEvents)
        {
            var matches = hooks.TryGetProperty(eventName, out var groups)
                ? GetStatusLabHandlers(groups).ToArray() : [];
            if (matches.Length == 0) { problems.Add($"missing canonical handler {eventName}"); continue; }
            if (matches.Length > 1) problems.Add($"duplicate Status Lab handler {eventName}");
            foreach (var command in matches.Select(GetCommand))
            {
                var target = ExtractFileTarget(command);
                string? targetFull = null;
                try { if (target is not null) targetFull = Path.GetFullPath(target); }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
                if (targetFull is null || !string.Equals(targetFull, StableLoggerPath, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"path drift {eventName}");
                if (target is not null && IsTransientPath(target)) problems.Add($"transient numbered build path {eventName}");
                if (targetFull is null || !File.Exists(targetFull)) problems.Add($"target missing {eventName}");
            }
        }
        return problems.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<JsonElement> GetStatusLabHandlers(JsonElement groups)
    {
        if (groups.ValueKind != JsonValueKind.Array) yield break;
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("hooks", out var handlers) || handlers.ValueKind != JsonValueKind.Array) continue;
            foreach (var handler in handlers.EnumerateArray())
                if (GetCommand(handler).Contains("codex-hook-logger.ps1", StringComparison.OrdinalIgnoreCase)) yield return handler;
        }
    }

    private static string GetCommand(JsonElement handler)
    {
        foreach (var name in new[] { "commandWindows", "command_windows", "command" })
            if (handler.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string? ExtractFileTarget(string command)
    {
        var marker = command.IndexOf("-File", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var value = command[(marker + 5)..].TrimStart();
        if (value.StartsWith('"')) { var end = value.IndexOf('"', 1); return end > 1 ? value[1..end] : null; }
        return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static bool IsTransientPath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.Length >= 3 && part.EndsWith(')') && part.Contains('(') && part[^2] is >= '0' and <= '9');

    private static List<string> DetectHomes()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddIfPresent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try { var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value)); if (Directory.Exists(full) && seen.Add(full)) result.Add(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        AddIfPresent(Environment.GetEnvironmentVariable("CODEX_HOME"));
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddIfPresent(Path.Combine(user, ".codex-agentloop")); AddIfPresent(Path.Combine(user, ".codex"));
        try { foreach (var path in Directory.EnumerateDirectories(user, ".codex-*", SearchOption.TopDirectoryOnly)) AddIfPresent(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }
}
