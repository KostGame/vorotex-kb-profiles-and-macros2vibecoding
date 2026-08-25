using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal sealed record CodexHookHealthSnapshot(string Status, int HomesFound, int HealthyHomes, string Detail)
{
    public bool Healthy => HomesFound > 0 && HomesFound == HealthyHomes;
}

internal static class CodexHookHealth
{
    private static readonly string[] RequiredEvents =
    [
        "UserPromptSubmit", "PermissionRequest", "PreToolUse", "PostToolUse", "Stop", "SessionEnd"
    ];

    public static CodexHookHealthSnapshot Inspect()
    {
        var homes = DetectHomes();
        if (homes.Count == 0)
            return new("Не установлены", 0, 0, "Codex home не найден");

        var healthy = 0;
        var details = new List<string>();
        foreach (var home in homes)
        {
            var hooksPath = Path.Combine(home, "hooks.json");
            if (!File.Exists(hooksPath))
            {
                details.Add($"{Path.GetFileName(home)}: hooks.json отсутствует");
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(hooksPath));
                var missing = RequiredEvents.Where(name => !HasStatusLabHandler(document.RootElement, name)).ToArray();
                if (missing.Length == 0)
                {
                    healthy++;
                    details.Add($"{Path.GetFileName(home)}: OK");
                }
                else
                {
                    details.Add($"{Path.GetFileName(home)}: обновить ({string.Join(", ", missing)})");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                details.Add($"{Path.GetFileName(home)}: ошибка чтения");
            }
        }

        var status = healthy == homes.Count ? "Установлены · актуальны" : healthy == 0 ? "Нужно установить / обновить" : "Частично актуальны";
        return new(status, homes.Count, healthy, string.Join(" · ", details));
    }

    private static List<string> DetectHomes()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfPresent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
                if (Directory.Exists(full) && seen.Add(full))
                    result.Add(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        AddIfPresent(Environment.GetEnvironmentVariable("CODEX_HOME"));
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddIfPresent(Path.Combine(user, ".codex-agentloop"));
        AddIfPresent(Path.Combine(user, ".codex"));

        try
        {
            foreach (var path in Directory.EnumerateDirectories(user, ".codex-*", SearchOption.TopDirectoryOnly))
                AddIfPresent(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    private static bool HasStatusLabHandler(JsonElement root, string eventName)
    {
        if (!root.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object ||
            !hooks.TryGetProperty(eventName, out var groups) || groups.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("hooks", out var handlers) || handlers.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var handler in handlers.EnumerateArray())
            {
                foreach (var propertyName in new[] { "command", "commandWindows", "command_windows" })
                {
                    if (handler.TryGetProperty(propertyName, out var command) && command.ValueKind == JsonValueKind.String &&
                        (command.GetString() ?? string.Empty).Contains("codex-hook-logger.ps1", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }
}
