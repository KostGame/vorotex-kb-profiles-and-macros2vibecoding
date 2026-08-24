using System.Text.Json;
using Microsoft.Win32;

namespace Vorotex.K15.Hud;

internal sealed class HudConfig
{
    public int AutoHideMs { get; set; } = 9000;
    public string DefaultProfile { get; set; } = "B";
    public HotkeyOptions Hotkeys { get; set; } = HotkeyOptions.CreateDefault();
    public List<ProfileDefinition> Profiles { get; set; } = [];

    public static HudConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "profiles.json");
        if (File.Exists(path))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<HudConfig>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (parsed is { Profiles.Count: > 0 })
                {
                    parsed.Hotkeys ??= HotkeyOptions.CreateDefault();
                    return parsed;
                }
            }
            catch
            {
                // Fall back to the accepted built-in V1 map.
            }
        }

        return CreateDefault();
    }

    private static HudConfig CreateDefault() => new()
    {
        AutoHideMs = 9000,
        DefaultProfile = "B",
        Hotkeys = HotkeyOptions.CreateDefault(),
        Profiles =
        [
            ProfileDefinition.Create("A", "TOOLS / AUTH", new Dictionary<string, HudKeyDefinition>
            {
                ["1"] = new("COPY"), ["2"] = new("PASTE +\nNEW LINE"), ["3"] = new("CUT"),
                ["4"] = new("UNDO"), ["5"] = new("REDO"), ["6"] = new("SELECT ALL"),
                ["7"] = new("ОТЧЕТ"), ["8"] = new("ВОТ ОТЧЕТ"), ["9"] = new("```"),
                ["0"] = new("ОТЧЕТ ИЗ\nБУФЕРА"), ["."] = new("ДАЙ СТАТУС"),
                ["Enter"] = new("НОВАЯ СТРОКА", "flow"), ["-"] = new("СТОП", "flow"),
                ["+"] = new("ОТЧЕТ ДЛЯ\nСЛЕД. ЧАТА"), ["Space"] = new("ПОДТВЕРЖДАЮ", "primary"),
                ["Joystick"] = new("ОТПРАВИТЬ", "send")
            }),
            ProfileDefinition.Create("B", "MAIN / VIBECODING", new Dictionary<string, HudKeyDefinition>
            {
                ["1"] = new("ПРОВЕРЬ"), ["2"] = new("СЛЕДУЮЩИЙ\nШАГ"), ["3"] = new("СЛЕД. ПРОМПТ"),
                ["4"] = new("ИСПРАВЛЯЙ"), ["5"] = new("ПУБЛИКУЙ"), ["6"] = new("МЕРЖИ"),
                ["7"] = new("СОЗДАВАЙ"), ["8"] = new("ПРОДОЛЖАЙ"), ["9"] = new("РЕВЬЮ"),
                ["0"] = new("ГОТОВО"), ["."] = new("ДАЙ СТАТУС"),
                ["Enter"] = new("НОВАЯ СТРОКА", "flow"), ["-"] = new("СТОП", "flow"),
                ["+"] = new("ОТЧЕТ ДЛЯ\nСЛЕД. ЧАТА"), ["Space"] = new("ДАВАЙ ДАЛЬШЕ\nБЕЗ PUSH/MERGE", "primary"),
                ["Joystick"] = new("ОТПРАВИТЬ", "send")
            })
        ]
    };
}

internal sealed class HotkeyOptions
{
    public string Toggle { get; set; } = "Ctrl+Alt+K";
    public string CycleProfile { get; set; } = "Ctrl+Alt+P";
    public string ShowBoth { get; set; } = "Ctrl+Alt+Shift+K";

    public static HotkeyOptions CreateDefault() => new();
}

internal sealed class ProfileDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public Dictionary<string, HudKeyDefinition> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HudKeyDefinition GetKey(string key) => Keys.TryGetValue(key, out var definition)
        ? definition
        : new HudKeyDefinition("—");

    public static ProfileDefinition Create(string id, string title, Dictionary<string, HudKeyDefinition> keys) => new()
    {
        Id = id,
        Title = title,
        Keys = new Dictionary<string, HudKeyDefinition>(keys, StringComparer.OrdinalIgnoreCase)
    };
}

internal sealed class HudKeyDefinition
{
    public string Action { get; set; } = string.Empty;
    public string? Accent { get; set; }

    public HudKeyDefinition() { }

    public HudKeyDefinition(string action, string? accent = null)
    {
        Action = action;
        Accent = accent;
    }
}

internal static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VorotexK15Hud";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Не удалось открыть HKCU Run.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к программе.");
        key.SetValue(ValueName, $"\"{executable}\"");
    }
}
