using System.Text.Json;
using Microsoft.Win32;

namespace Vorotex.K15.Hud;

internal sealed class HudConfig
{
    public int AutoHideMs { get; set; } = 9000;
    public string DefaultProfile { get; set; } = "B";
    public HotkeyOptions Hotkeys { get; set; } = HotkeyOptions.CreateDefault();
    public OverlayOptions Overlay { get; set; } = OverlayOptions.CreateDefault();
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
                    parsed.Overlay ??= OverlayOptions.CreateDefault();
                    return parsed;
                }
            }
            catch
            {
                // Fall back to the accepted built-in V1.2 RC1 map.
            }
        }

        return CreateDefault();
    }

    private static HudConfig CreateDefault() => new()
    {
        AutoHideMs = 9000,
        DefaultProfile = "B",
        Hotkeys = HotkeyOptions.CreateDefault(),
        Overlay = OverlayOptions.CreateDefault(),
        Profiles =
        [
            ProfileDefinition.Create("A", "TOOLS / AUTH", "red", new Dictionary<string, HudKeyDefinition>
            {
                ["1"] = new("COPY"), ["2"] = new("PASTE + НОВАЯ СТРОКА", label: "PASTE +\nНОВАЯ\nСТРОКА"), ["3"] = new("CUT"),
                ["4"] = new("UNDO"), ["5"] = new("REDO"), ["6"] = new("SELECT ALL", label: "SELECT\nALL"),
                ["7"] = new("ОТЧЕТ"), ["8"] = new("ВОТ ОТЧЕТ", label: "ВОТ\nОТЧЕТ"), ["9"] = new("```"),
                ["0"] = new("ОТЧЕТ ИЗ БУФЕРА", label: "ОТЧЕТ ИЗ\nБУФЕРА"),
                ["."] = new("ДАЙ СТАТУС: ЧТО СДЕЛАНО, ЧТО ОСТАЛОСЬ, БЛОКЕРЫ И СЛЕДУЮЩИЙ ШАГ", label: "ПОЛНЫЙ\nСТАТУС"),
                ["Enter"] = new("НОВАЯ СТРОКА (SHIFT+ENTER)", "flow", "НОВАЯ\nСТРОКА"), ["-"] = new("СТОП", "flow"),
                ["+"] = new("ПОДГОТОВЬ ОТЧЕТ ДЛЯ СЛЕДУЮЩЕГО ЧАТА", label: "ОТЧЕТ\nДЛЯ СЛЕД.\nЧАТА"),
                ["Space"] = new("ПОДТВЕРЖДАЮ", "primary"),
                ["Joystick"] = new("ОТПРАВИТЬ", "send", "ОТПРАВИТЬ"),
                ["Encoder"] = new("ВЕРТИКАЛЬНЫЙ СКРОЛЛ", label: "СКРОЛЛ")
            }),
            ProfileDefinition.Create("B", "MAIN / VIBECODING", "blue", new Dictionary<string, HudKeyDefinition>
            {
                ["1"] = new("ПРОВЕРЬ"), ["2"] = new("СЛЕДУЮЩИЙ ШАГ", label: "СЛЕДУЮЩИЙ\nШАГ"),
                ["3"] = new("ПИШИ СЛЕДУЮЩИЙ ПРОМПТ ДЛЯ АГЕНТА", label: "СЛЕД. ПРОМПТ\nАГЕНТУ"),
                ["4"] = new("ИСПРАВЛЯЙ"), ["5"] = new("ПУБЛИКУЙ"), ["6"] = new("МЕРЖИ"),
                ["7"] = new("СОЗДАВАЙ"), ["8"] = new("ПРОДОЛЖАЙ"), ["9"] = new("ПРОВЕДИ РЕВЬЮ", label: "ПРОВЕДИ\nРЕВЬЮ"),
                ["0"] = new("ГОТОВО"), ["."] = new("ДАЙ СТАТУС", label: "ДАЙ\nСТАТУС"),
                ["Enter"] = new("НОВАЯ СТРОКА (SHIFT+ENTER)", "flow", "НОВАЯ\nСТРОКА"), ["-"] = new("СТОП", "flow"),
                ["+"] = new("ПРИНИМАЕТСЯ", "primary"),
                ["Space"] = new("ДАВАЙ ДАЛЬШЕ, БЕЗ PUSH/MERGE", "primary", "ДАЛЬШЕ\nБЕЗ PUSH/MERGE"),
                ["Joystick"] = new("ОТПРАВИТЬ", "send", "ОТПРАВИТЬ")
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

internal sealed class OverlayOptions
{
    public string Size { get; set; } = "medium";
    public string Position { get; set; } = "aboveCursor";

    public static OverlayOptions CreateDefault() => new();

    public float GetSizeScale() => Size.Trim().ToLowerInvariant() switch
    {
        "small" => 0.78f,
        "large" => 1.25f,
        _ => 1.0f
    };

    public OverlayPosition GetPosition() => Position.Trim().ToLowerInvariant() switch
    {
        "bottomright" => OverlayPosition.BottomRight,
        "bottom-right" => OverlayPosition.BottomRight,
        "bottom_right" => OverlayPosition.BottomRight,
        _ => OverlayPosition.AboveCursor
    };
}

internal enum OverlayPosition
{
    AboveCursor,
    BottomRight
}

internal sealed class ProfileDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Color { get; set; } = "teal";
    public Dictionary<string, HudKeyDefinition> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HudKeyDefinition GetKey(string key) => Keys.TryGetValue(key, out var definition)
        ? definition
        : new HudKeyDefinition("—");

    public bool TryGetKey(string key, out HudKeyDefinition definition) =>
        Keys.TryGetValue(key, out definition!);

    public static ProfileDefinition Create(
        string id,
        string title,
        string color,
        Dictionary<string, HudKeyDefinition> keys) => new()
    {
        Id = id,
        Title = title,
        Color = color,
        Keys = new Dictionary<string, HudKeyDefinition>(keys, StringComparer.OrdinalIgnoreCase)
    };
}

internal sealed class HudKeyDefinition
{
    public string Action { get; set; } = string.Empty;
    public string? Accent { get; set; }
    public string? Label { get; set; }

    public string DisplayText => string.IsNullOrWhiteSpace(Label) ? Action : Label;

    public HudKeyDefinition() { }

    public HudKeyDefinition(string action, string? accent = null, string? label = null)
    {
        Action = action;
        Accent = accent;
        Label = label;
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
