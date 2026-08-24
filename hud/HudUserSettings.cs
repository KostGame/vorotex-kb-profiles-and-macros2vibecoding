using System.Text.Json;

namespace Vorotex.K15.Hud;

internal sealed class HudUserSettings
{
    private const string SettingsDirectoryName = "K15 HUD";
    private const string SettingsFileName = "settings.json";

    public string Size { get; set; } = "medium";
    public string Position { get; set; } = "aboveCursor";

    public static HudUserSettings Load(OverlayOptions defaults)
    {
        var normalizedDefaults = defaults.CloneNormalized();
        var fallback = new HudUserSettings
        {
            Size = normalizedDefaults.Size,
            Position = normalizedDefaults.Position
        };

        try
        {
            if (!File.Exists(SettingsPath))
                return fallback;

            var parsed = JsonSerializer.Deserialize<HudUserSettings>(
                File.ReadAllText(SettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed is null)
                return fallback;

            parsed.Size = OverlayOptions.NormalizeSize(parsed.Size, fallback.Size);
            parsed.Position = OverlayOptions.NormalizePosition(parsed.Position, fallback.Position);
            return parsed;
        }
        catch
        {
            return fallback;
        }
    }

    public OverlayOptions ToOverlayOptions() => new()
    {
        Size = OverlayOptions.NormalizeSize(Size),
        Position = OverlayOptions.NormalizePosition(Position)
    };

    public void Save()
    {
        Size = OverlayOptions.NormalizeSize(Size);
        Position = OverlayOptions.NormalizePosition(Position);

        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("Не удалось определить папку настроек.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, SettingsPath, true);
    }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VOROTEX",
        SettingsDirectoryName,
        SettingsFileName);
}
