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

    public static CodexHookHealthSnapshot Inspect() => InspectHomes(CodexHomeDiscovery.DetectHomes());

    internal static CodexHookHealthSnapshot InspectHomes(IReadOnlyList<string> homes)
    {
        if (homes.Count == 0) return new("Не установлены", 0, 0, "Codex home не найден");

        var outcomes = new List<HomeValidation>();
        foreach (var home in homes)
        {
            var problems = new List<string>();
            var actualIds = new HashSet<string>(StringComparer.Ordinal);
            var expectedId = CodexSourceIdentity.ForHome(home);
            if (!CodexSourceIdentity.IsValid(expectedId))
            {
                problems.Add("sourceInstanceId cannot be generated");
                outcomes.Add(new(home, problems, actualIds));
                continue;
            }

            var hooksPath = Path.Combine(home, "hooks.json");
            if (!File.Exists(hooksPath))
            {
                problems.Add("hooks.json отсутствует");
                outcomes.Add(new(home, problems, actualIds));
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(hooksPath));
                problems.AddRange(Validate(document.RootElement, expectedId!, actualIds));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                problems.Add("malformed hooks.json");
            }

            outcomes.Add(new(home, problems, actualIds));
        }

        var duplicateIds = outcomes.SelectMany(outcome => outcome.ActualIds)
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var details = new List<string>();
        var healthy = 0;
        foreach (var outcome in outcomes)
        {
            foreach (var duplicate in outcome.ActualIds.Where(duplicateIds.Contains))
                outcome.Problems.Add($"duplicate sourceInstanceId {duplicate}");
            if (outcome.Problems.Count == 0)
            {
                healthy++;
                details.Add($"{outcome.Home}: OK");
            }
            else
            {
                details.Add($"{outcome.Home}: обновить ({string.Join(", ", outcome.Problems.Distinct(StringComparer.Ordinal))})");
            }
        }

        var status = healthy == outcomes.Count ? "Установлены · актуальны" :
            healthy == 0 ? "Нужно установить / обновить" : "Частично актуальны";
        return new(status, outcomes.Count, healthy, string.Join(" · ", details));
    }

    private sealed record HomeValidation(string Home, List<string> Problems, HashSet<string> ActualIds);

    private static List<string> Validate(JsonElement root, string expectedId, HashSet<string> actualIds)
    {
        var problems = new List<string>();
        if (!root.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object)
        {
            problems.Add("missing hooks object");
            return problems;
        }

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
            if (matches.Length == 0)
            {
                problems.Add($"missing canonical handler {eventName}");
                continue;
            }
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

                var sourceId = ExtractSourceInstanceId(command);
                if (!CodexSourceIdentity.IsValid(sourceId))
                    problems.Add($"missing or malformed sourceInstanceId {eventName}");
                else
                {
                    actualIds.Add(sourceId!);
                    if (!string.Equals(sourceId, expectedId, StringComparison.Ordinal))
                        problems.Add($"wrong sourceInstanceId {eventName}");
                }
            }
        }
        return problems;
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

    private static string? ExtractSourceInstanceId(string command)
    {
        var marker = command.IndexOf("-SourceInstanceId", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var value = command[(marker + "-SourceInstanceId".Length)..].TrimStart();
        if (value.StartsWith('"')) { var end = value.IndexOf('"', 1); return end > 1 ? value[1..end] : null; }
        return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static bool IsTransientPath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.Length >= 3 && part.EndsWith(')') && part.Contains('(') && part[^2] is >= '0' and <= '9');
}
