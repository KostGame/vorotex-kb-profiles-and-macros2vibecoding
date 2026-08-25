using System.Text;

namespace Vorotex.K15.StatusLab;

internal enum K15LightingMode
{
    Constant,
    FlowingWater,
    MonoWater, // OEM UI calls native mode 0x83 "Horse race"; old Status Lab called it mono_water.
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

internal enum PaletteSource
{
    Profile,
    ProfilePair
}

internal sealed class LightingEffectConfig
{
    public bool Enabled { get; set; } = true;
    public K15LightingMode Mode { get; set; } = K15LightingMode.SingleColorBreathing;
    public PaletteSource Palette { get; set; } = PaletteSource.Profile;
    public int Brightness { get; set; } = 5;
    public int Speed { get; set; } = 4;
    public int Direction { get; set; }
    public double DurationSeconds { get; set; }

    public string[] Colors { get; set; } = [];
    public byte? PaletteMask { get; set; }

    public LightingEffectConfig Clone() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        Palette = Palette,
        Brightness = Brightness,
        Speed = Speed,
        Direction = Direction,
        DurationSeconds = DurationSeconds,
        Colors = Colors.ToArray(),
        PaletteMask = PaletteMask
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
    public const int CurrentSchemaVersion = 3;
    public const int MaxNotifierColors = 2;
    public static string FilePath { get; } = Path.Combine(EventJournal.DirectoryPath, "config.toml");

    public string? LoadWarning { get; private set; }
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public WireColorOrder WireColorOrder { get; set; } = WireColorOrder.RGB;
    public double DoneAttentionTimeoutSeconds { get; set; } = 15;
    public ProfileSetConfig Profiles { get; set; } = new();
    public StateLightingConfig States { get; set; } = new();
    public LightingEffectConfig ProfileSwitch { get; set; } = new();
    public LightingEffectConfig StopSignal { get; set; } = new();
    public LightingEffectConfig ActivationSignal { get; set; } = new();
    public double EffectLabDurationSeconds { get; set; } = 4;

    public static StatusLabConfig CreateDefault() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        WireColorOrder = WireColorOrder.RGB,
        DoneAttentionTimeoutSeconds = 15,
        Profiles = new ProfileSetConfig
        {
            A = new ProfileLightingConfig { Color = "#FF0000" },
            B = new ProfileLightingConfig { Color = "#0000FF" }
        },
        States = new StateLightingConfig
        {
            Running = Effect(K15LightingMode.FlowingWater, PaletteSource.Profile, 4, 3, 0, 0),
            Waiting = Effect(K15LightingMode.SingleColorBreathing, PaletteSource.Profile, 6, 7, 0, 0),
            Done = Effect(K15LightingMode.SingleColorBreathing, PaletteSource.Profile, 6, 5, 0, 0),
            Error = Effect(K15LightingMode.SingleColorBreathing, PaletteSource.Profile, 6, 7, 0, 0, enabled: false)
        },
        ProfileSwitch = Effect(K15LightingMode.FlowingWater, PaletteSource.Profile, 5, 5, 0, 4),
        StopSignal = Effect(K15LightingMode.CycleBreathing, PaletteSource.ProfilePair, 6, 7, 0, 3),
        ActivationSignal = Effect(K15LightingMode.FlowingWater, PaletteSource.ProfilePair, 5, 5, 0, 3),
        EffectLabDurationSeconds = 4
    };

    private static LightingEffectConfig Effect(K15LightingMode mode, PaletteSource palette, int brightness,
        int speed, int direction, double duration, bool enabled = true) => new()
    {
        Enabled = enabled,
        Mode = mode,
        Palette = palette,
        Brightness = brightness,
        Speed = speed,
        Direction = direction,
        DurationSeconds = duration
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
                $"RGB config invalid: {ex.Message}. Existing config.toml was preserved unchanged; safe defaults are active for this run.";
            return fallback;
        }
    }

    public static void EnsureExists()
    {
        Directory.CreateDirectory(EventJournal.DirectoryPath);
        if (File.Exists(FilePath))
            return;
        File.WriteAllText(FilePath, ConfigToml.Serialize(CreateDefault()), new UTF8Encoding(false));
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
        rendered.Colors = source.Mode == K15LightingMode.Off
            ? []
            : source.Palette switch
            {
                PaletteSource.Profile => [GetProfile(onboardSlot).Color],
                PaletteSource.ProfilePair => [Profiles.A.Color, Profiles.B.Color],
                _ => throw new ArgumentOutOfRangeException(nameof(source.Palette))
            };
        rendered.PaletteMask = null;
        return rendered;
    }

    public void Validate()
    {
        if (SchemaVersion is not (2 or CurrentSchemaVersion))
            throw new InvalidDataException(
                $"Unsupported schema_version {SchemaVersion}; expected 2 or {CurrentSchemaVersion}.");

        _ = ParseColor(Profiles.A.Color);
        _ = ParseColor(Profiles.B.Color);
        ValidateEffect(States.Running, "states.running");
        ValidateEffect(States.Waiting, "states.waiting");
        ValidateEffect(States.Done, "states.done");
        ValidateEffect(States.Error, "states.error");
        ValidateEffect(ProfileSwitch, "profile_switch");
        ValidateEffect(StopSignal, "stop_signal");
        ValidateEffect(ActivationSignal, "activation");

        if (DoneAttentionTimeoutSeconds is < 0 or > 3600)
            throw new InvalidDataException("behavior.done_attention_timeout_seconds must be 0..3600.");
        if (EffectLabDurationSeconds is < 0.5 or > 30)
            throw new InvalidDataException("effect_lab.test_duration_seconds must be 0.5..30.");
    }

    internal void NormalizeLegacySchema()
    {
        if (SchemaVersion == 2)
            SchemaVersion = CurrentSchemaVersion;
    }

    internal static bool IsControlledPaletteMode(K15LightingMode mode) => mode is
        K15LightingMode.Constant or
        K15LightingMode.FlowingWater or
        K15LightingMode.SingleColorBreathing or
        K15LightingMode.CycleBreathing or
        K15LightingMode.Off;

    private static void ValidateEffect(LightingEffectConfig effect, string path)
    {
        if (!IsControlledPaletteMode(effect.Mode))
            throw new InvalidDataException(
                $"{path}.effect '{ModeName(effect.Mode)}' is not allowed for Status Lab notifier. " +
                "Use constant, flowing_water, single_color_breathing, cycle_breathing or off; " +
                "research other native modes in Lighting Lab.");
        if (effect.Brightness is < 1 or > 6)
            throw new InvalidDataException($"{path}.brightness must be 1..6.");
        if (effect.Speed is < 1 or > 7)
            throw new InvalidDataException($"{path}.speed must be 1..7.");
        if (effect.Direction is < 0 or > 1)
            throw new InvalidDataException($"{path}.direction must be 0 or 1.");
        if (effect.DurationSeconds is < 0 or > 3600)
            throw new InvalidDataException($"{path}.duration_seconds must be 0..3600.");
    }

    public static string PaletteName(PaletteSource palette) => palette switch
    {
        PaletteSource.Profile => "profile",
        PaletteSource.ProfilePair => "profile_pair",
        _ => throw new ArgumentOutOfRangeException(nameof(palette))
    };

    public static PaletteSource ParsePaletteName(string value) => value.Trim().ToLowerInvariant() switch
    {
        "profile" => PaletteSource.Profile,
        "profile_pair" => PaletteSource.ProfilePair,
        _ => throw new InvalidDataException($"Unknown palette source '{value}'. Use profile or profile_pair.")
    };

    public static string ModeName(K15LightingMode mode) => mode switch
    {
        K15LightingMode.Constant => "constant",
        K15LightingMode.FlowingWater => "flowing_water",
        K15LightingMode.MonoWater => "horse_race",
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
        "horse_race" or "mono_water" => K15LightingMode.MonoWater,
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
