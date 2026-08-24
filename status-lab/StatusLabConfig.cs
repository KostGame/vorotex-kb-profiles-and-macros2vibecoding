using System.Text;

namespace Vorotex.K15.StatusLab;

internal enum K15LightingMode
{
    Constant,
    FlowingWater,
    MonoWater,
    SingleColorBreathing,
    CycleBreathing,
    TetrisBlocks,
    Neon,
    Ambilight,
    Off
}

internal enum WireColorOrder
{
    RGB,
    GRB
}

internal sealed class LightingEffectConfig
{
    public bool Enabled { get; set; } = true;
    public K15LightingMode Mode { get; set; } = K15LightingMode.SingleColorBreathing;
    public int Brightness { get; set; } = 5;
    public int Speed { get; set; } = 4;
    public int Direction { get; set; }
    public double DurationSeconds { get; set; }

    // Runtime-only palette. Canonical TOML deliberately has no color under states/overlays.
    public string[] Colors { get; set; } = [];

    public LightingEffectConfig Clone() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        Brightness = Brightness,
        Speed = Speed,
        Direction = Direction,
        DurationSeconds = DurationSeconds,
        Colors = Colors.ToArray()
    };
}

internal sealed class ProfileLightingConfig
{
    public string Color { get; set; } = "#FFFFFF";
}

internal sealed class StateLightingConfig
{
    public LightingEffectConfig Running { get; set; } = new();
    public LightingEffectConfig Waiting { get; set; } = new();
    public LightingEffectConfig Done { get; set; } = new();
    public LightingEffectConfig Error { get; set; } = new();
}

internal sealed class ProfileSetConfig
{
    public ProfileLightingConfig A { get; set; } = new();
    public ProfileLightingConfig B { get; set; } = new();
}

internal sealed class StatusLabConfig
{
    public static string FilePath { get; } = Path.Combine(EventJournal.DirectoryPath, "config.toml");

    private static readonly HashSet<K15LightingMode> ControlledPaletteModes =
    [
        K15LightingMode.Constant,
        K15LightingMode.FlowingWater,
        K15LightingMode.MonoWater,
        K15LightingMode.SingleColorBreathing,
        K15LightingMode.Off
    ];

    public const int MaxNotifierColors = 2;

    public string? LoadWarning { get; private set; }

    public int SchemaVersion { get; set; } = 2;
    public WireColorOrder WireColorOrder { get; set; } = WireColorOrder.RGB;
    public ProfileSetConfig Profiles { get; set; } = new();
    public StateLightingConfig States { get; set; } = new();
    public LightingEffectConfig ProfileSwitch { get; set; } = new();
    public LightingEffectConfig ActivationSignal { get; set; } = new();
    public double EffectLabDurationSeconds { get; set; } = 4;

    public static StatusLabConfig CreateDefault() => new()
    {
        SchemaVersion = 2,
        WireColorOrder = WireColorOrder.RGB,
        Profiles = new ProfileSetConfig
        {
            A = new ProfileLightingConfig { Color = "#FF0000" },
            B = new ProfileLightingConfig { Color = "#0000FF" }
        },
        States = new StateLightingConfig
        {
            Running = new LightingEffectConfig
            {
                Mode = K15LightingMode.MonoWater,
                Brightness = 4,
                Speed = 3,
                Direction = 0,
                DurationSeconds = 0
            },
            Waiting = new LightingEffectConfig
            {
                Mode = K15LightingMode.SingleColorBreathing,
                Brightness = 6,
                Speed = 6,
                Direction = 0,
                DurationSeconds = 0
            },
            Done = new LightingEffectConfig
            {
                Mode = K15LightingMode.SingleColorBreathing,
                Brightness = 6,
                Speed = 3,
                Direction = 0,
                DurationSeconds = 10
            },
            Error = new LightingEffectConfig
            {
                Mode = K15LightingMode.SingleColorBreathing,
                Brightness = 6,
                Speed = 7,
                Direction = 0,
                DurationSeconds = 15
            }
        },
        ProfileSwitch = new LightingEffectConfig
        {
            Mode = K15LightingMode.FlowingWater,
            Brightness = 6,
            Speed = 5,
            Direction = 0,
            DurationSeconds = 2
        },
        ActivationSignal = new LightingEffectConfig
        {
            Enabled = false,
            Mode = K15LightingMode.MonoWater,
            Brightness = 4,
            Speed = 4,
            Direction = 0,
            DurationSeconds = 2
        },
        EffectLabDurationSeconds = 4
    };

    public static StatusLabConfig LoadOrCreate()
    {
        EnsureExists();
        try
        {
            return ConfigToml.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            var fallback = CreateDefault();
            fallback.LoadWarning =
                $"RGB config invalid: {ex.Message}. Existing config.toml was preserved unchanged; defaults are active for this run.";
            return fallback;
        }
    }

    public static void EnsureExists()
    {
        Directory.CreateDirectory(EventJournal.DirectoryPath);
        if (File.Exists(FilePath))
            return;

        var config = CreateDefault();
        File.WriteAllText(FilePath, ConfigToml.Serialize(config), new UTF8Encoding(false));
    }

    public ProfileLightingConfig GetProfile(byte onboardSlot) => onboardSlot switch
    {
        0 => Profiles.A,
        1 => Profiles.B,
        _ => throw new ArgumentOutOfRangeException(nameof(onboardSlot))
    };

    public LightingEffectConfig GetState(K15NormalizedState state) => state switch
    {
        K15NormalizedState.Running => States.Running,
        K15NormalizedState.Waiting => States.Waiting,
        K15NormalizedState.DonePendingAttention => States.Done,
        K15NormalizedState.Error => States.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(state), "NORMAL restores the exact device baseline.")
    };

    public LightingEffectConfig RenderForProfile(byte onboardSlot, LightingEffectConfig source)
    {
        var rendered = source.Clone();
        rendered.Colors = [GetProfile(onboardSlot).Color];
        if (rendered.Colors.Length > MaxNotifierColors)
            throw new InvalidDataException($"Notifier palettes are limited to {MaxNotifierColors} colors.");
        return rendered;
    }

    public void Validate()
    {
        if (SchemaVersion != 2)
            throw new InvalidDataException($"Unsupported schema_version {SchemaVersion}; expected 2.");

        _ = ParseColor(Profiles.A.Color);
        _ = ParseColor(Profiles.B.Color);
        ValidateEffect(States.Running, "states.running");
        ValidateEffect(States.Waiting, "states.waiting");
        ValidateEffect(States.Done, "states.done");
        ValidateEffect(States.Error, "states.error");
        ValidateEffect(ProfileSwitch, "profile_switch");
        ValidateEffect(ActivationSignal, "activation");

        if (EffectLabDurationSeconds is < 0.5 or > 30)
            throw new InvalidDataException("effect_lab.test_duration_seconds must be 0.5..30.");
    }

    public static bool IsControlledPaletteMode(K15LightingMode mode) => ControlledPaletteModes.Contains(mode);

    private static void ValidateEffect(LightingEffectConfig effect, string path)
    {
        if (!IsControlledPaletteMode(effect.Mode))
            throw new InvalidDataException(
                $"{path}.effect '{ModeName(effect.Mode)}' is not allowed for notifier use: only controlled 1-2 color effects are permitted.");
        if (effect.Brightness is < 1 or > 6)
            throw new InvalidDataException($"{path}.brightness must be 1..6.");
        if (effect.Speed is < 1 or > 7)
            throw new InvalidDataException($"{path}.speed must be 1..7.");
        if (effect.Direction is < 0 or > 1)
            throw new InvalidDataException($"{path}.direction must be 0 or 1.");
        if (effect.DurationSeconds is < 0 or > 3600)
            throw new InvalidDataException($"{path}.duration_seconds must be 0..3600.");
    }

    public static string ModeName(K15LightingMode mode) => mode switch
    {
        K15LightingMode.Constant => "constant",
        K15LightingMode.FlowingWater => "flowing_water",
        K15LightingMode.MonoWater => "mono_water",
        K15LightingMode.SingleColorBreathing => "single_color_breathing",
        K15LightingMode.CycleBreathing => "cycle_breathing",
        K15LightingMode.TetrisBlocks => "tetris_blocks",
        K15LightingMode.Neon => "neon",
        K15LightingMode.Ambilight => "ambilight",
        K15LightingMode.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static K15LightingMode ParseModeName(string value) => value.Trim().ToLowerInvariant() switch
    {
        "constant" => K15LightingMode.Constant,
        "flowing_water" => K15LightingMode.FlowingWater,
        "mono_water" => K15LightingMode.MonoWater,
        "single_color_breathing" => K15LightingMode.SingleColorBreathing,
        "cycle_breathing" => K15LightingMode.CycleBreathing,
        "tetris_blocks" => K15LightingMode.TetrisBlocks,
        "neon" => K15LightingMode.Neon,
        "ambilight" => K15LightingMode.Ambilight,
        "off" => K15LightingMode.Off,
        _ => throw new InvalidDataException($"Unknown effect '{value}'.")
    };

    public static (byte R, byte G, byte B) ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Color cannot be empty.");

        return value.Trim().ToLowerInvariant() switch
        {
            "red" => (0xFF, 0x00, 0x00),
            "green" => (0x00, 0xFF, 0x00),
            "blue" => (0x00, 0x00, 0xFF),
            "white" => (0xFF, 0xFF, 0xFF),
            "black" => (0x00, 0x00, 0x00),
            "cyan" => (0x00, 0xFF, 0xFF),
            "magenta" or "purple" => (0xFF, 0x00, 0xFF),
            "yellow" => (0xFF, 0xFF, 0x00),
            _ => ParseHex(value)
        };
    }

    private static (byte R, byte G, byte B) ParseHex(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            throw new InvalidDataException($"Invalid color '{value}'. Use a named color or #RRGGBB.");

        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}