using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public int Direction { get; set; } = 0;
    public double DurationSeconds { get; set; } = 0;
    public string[] Colors { get; set; } = ["white"];

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
    public LightingEffectConfig Normal { get; set; } = new();
    public LightingEffectConfig SwitchSignal { get; set; } = new();
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
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(EventJournal.DirectoryPath, "config.json");

    [JsonIgnore]
    public string? LoadWarning { get; private set; }

    public int SchemaVersion { get; set; } = 1;
    public WireColorOrder WireColorOrder { get; set; } = WireColorOrder.RGB;
    public LightingEffectConfig ActivationSignal { get; set; } = new();
    public ProfileSetConfig Profiles { get; set; } = new();
    public StateLightingConfig States { get; set; } = new();

    public static StatusLabConfig CreateDefault() => new()
    {
        SchemaVersion = 1,

        // Physical K15 canary proved that writing semantic red as the first channel produces
        // actual red on this hardware. The older GRB assumption produced green profile-A flashes.
        WireColorOrder = WireColorOrder.RGB,

        // One-time visual handshake when RGB notifier mode is enabled.
        ActivationSignal = new LightingEffectConfig
        {
            Enabled = true,
            Mode = K15LightingMode.FlowingWater,
            Brightness = 4,
            Speed = 7,
            Direction = 0,
            DurationSeconds = 3,
            Colors = ["red", "blue"]
        },

        Profiles = new ProfileSetConfig
        {
            A = new ProfileLightingConfig
            {
                Normal = new LightingEffectConfig
                {
                    Mode = K15LightingMode.Constant,
                    Brightness = 6,
                    Speed = 7,
                    DurationSeconds = 0,
                    Colors = ["red"]
                },
                SwitchSignal = new LightingEffectConfig
                {
                    Mode = K15LightingMode.SingleColorBreathing,
                    Brightness = 6,
                    Speed = 7,
                    DurationSeconds = 5,
                    Colors = ["red"]
                }
            },
            B = new ProfileLightingConfig
            {
                Normal = new LightingEffectConfig
                {
                    Mode = K15LightingMode.Constant,
                    Brightness = 6,
                    Speed = 7,
                    DurationSeconds = 0,
                    Colors = ["blue"]
                },
                SwitchSignal = new LightingEffectConfig
                {
                    Mode = K15LightingMode.SingleColorBreathing,
                    Brightness = 6,
                    Speed = 7,
                    DurationSeconds = 5,
                    Colors = ["blue"]
                }
            }
        },

        States = new StateLightingConfig
        {
            Running = new LightingEffectConfig
            {
                Mode = K15LightingMode.TetrisBlocks,
                Brightness = 5,
                Speed = 7,
                Direction = 0,
                DurationSeconds = 0,
                Colors = ["white"]
            },
            Waiting = new LightingEffectConfig
            {
                Mode = K15LightingMode.SingleColorBreathing,
                Brightness = 6,
                Speed = 7,
                Direction = 0,
                DurationSeconds = 0,
                Colors = ["white"]
            },
            Done = new LightingEffectConfig
            {
                Mode = K15LightingMode.SingleColorBreathing,
                Brightness = 6,
                Speed = 4,
                Direction = 0,
                DurationSeconds = 15,
                Colors = ["green"]
            },
            Error = new LightingEffectConfig
            {
                Mode = K15LightingMode.SingleColorBreathing,
                Brightness = 6,
                Speed = 7,
                Direction = 0,
                DurationSeconds = 15,
                Colors = ["red"]
            }
        }
    };

    public static StatusLabConfig LoadOrCreate()
    {
        Directory.CreateDirectory(EventJournal.DirectoryPath);

        if (!File.Exists(FilePath))
        {
            var created = CreateDefault();
            Save(created);
            return created;
        }

        try
        {
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<StatusLabConfig>(json, JsonOptions)
                ?? throw new InvalidDataException("Config JSON deserialized to null.");
            config.NormalizeAndValidate();
            return config;
        }
        catch (Exception ex)
        {
            var backup = FilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            try
            {
                File.Copy(FilePath, backup, overwrite: false);
            }
            catch
            {
                backup = "backup failed";
            }

            var fallback = CreateDefault();
            fallback.LoadWarning = $"RGB config was invalid and defaults were loaded: {ex.Message}. Backup: {backup}";
            Save(fallback);
            return fallback;
        }
    }

    public static void Save(StatusLabConfig config)
    {
        config.NormalizeAndValidate();
        Directory.CreateDirectory(EventJournal.DirectoryPath);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(FilePath, json + Environment.NewLine, new UTF8Encoding(false));
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
        _ => throw new ArgumentOutOfRangeException(nameof(state), "NORMAL uses the active profile normal effect.")
    };

    private void NormalizeAndValidate()
    {
        if (SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported config schemaVersion {SchemaVersion}; expected 1.");

        ValidateEffect(ActivationSignal, "activationSignal");
        ValidateEffect(Profiles.A.Normal, "profiles.A.normal");
        ValidateEffect(Profiles.A.SwitchSignal, "profiles.A.switchSignal");
        ValidateEffect(Profiles.B.Normal, "profiles.B.normal");
        ValidateEffect(Profiles.B.SwitchSignal, "profiles.B.switchSignal");
        ValidateEffect(States.Running, "states.running");
        ValidateEffect(States.Waiting, "states.waiting");
        ValidateEffect(States.Done, "states.done");
        ValidateEffect(States.Error, "states.error");
    }

    private static void ValidateEffect(LightingEffectConfig effect, string path)
    {
        if (effect.Brightness is < 1 or > 6)
            throw new InvalidDataException($"{path}.brightness must be 1..6.");
        if (effect.Speed is < 1 or > 7)
            throw new InvalidDataException($"{path}.speed must be 1..7.");
        if (effect.Direction is < 0 or > 1)
            throw new InvalidDataException($"{path}.direction must be 0 or 1.");
        if (effect.DurationSeconds is < 0 or > 3600)
            throw new InvalidDataException($"{path}.durationSeconds must be 0..3600.");

        effect.Colors ??= [];
        if (effect.Colors.Length > 7)
            throw new InvalidDataException($"{path}.colors supports at most 7 entries.");
        if (effect.Mode != K15LightingMode.Off && effect.Colors.Length == 0)
            throw new InvalidDataException($"{path}.colors must contain at least one color unless mode=Off.");

        foreach (var color in effect.Colors)
            _ = ParseColor(color);
    }

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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
