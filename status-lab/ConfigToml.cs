using System.Globalization;
using System.Text;

namespace Vorotex.K15.StatusLab;

internal static class ConfigToml
{
    private static readonly HashSet<string> KnownSections = new(StringComparer.Ordinal)
    {
        "device", "behavior", "profiles.A", "profiles.B", "states.running", "states.waiting",
        "states.done", "states.error", "profile_switch", "stop_signal", "activation", "effect_lab"
    };

    public static StatusLabConfig Parse(string text)
    {
        var config = StatusLabConfig.CreateDefault();
        var section = string.Empty;
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = StripComment(lines[index]).Trim();
            if (raw.Length == 0)
                continue;

            if (raw.StartsWith('[') && raw.EndsWith(']'))
            {
                section = raw[1..^1].Trim();
                if (!KnownSections.Contains(section))
                    Fail(index, $"unknown section [{section}]");
                continue;
            }

            var equals = raw.IndexOf('=');
            if (equals <= 0)
                Fail(index, "expected key = value");

            var key = raw[..equals].Trim();
            var value = raw[(equals + 1)..].Trim();
            try
            {
                Apply(config, section, key, value);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException)
            {
                Fail(index, ex.Message);
            }
        }

        config.Validate();
        config.NormalizeLegacySchema();
        return config;
    }

    public static string Serialize(StatusLabConfig config)
    {
        config.NormalizeLegacySchema();
        config.Validate();
        var b = new StringBuilder();
        b.AppendLine("# VOROTEX K15 Status Lab");
        b.AppendLine("# Цвета принадлежат аппаратным профилям A/B. NORMAL восстанавливает exact baseline.");
        b.AppendLine("# palette = profile      -> цвет физически активного профиля");
        b.AppendLine("# palette = profile_pair -> два основных цвета: A затем B");
        b.AppendLine("# Production notifier modes: constant, flowing_water, single_color_breathing,");
        b.AppendLine("# cycle_breathing, off. Horse race, Tetris, Neon, Ambilight остаются в Lighting Lab.");
        b.AppendLine("# Status Lab НИКОГДА программно не переключает hardware Profile A/B.");
        b.AppendLine();
        b.AppendLine($"schema_version = {config.SchemaVersion}");
        b.AppendLine();
        b.AppendLine("[device]");
        b.AppendLine("# rgb доказан на текущей физической K15; grb оставлен для совместимости.");
        b.AppendLine($"wire_color_order = \"{config.WireColorOrder.ToString().ToLowerInvariant()}\"");
        b.AppendLine();
        b.AppendLine("[behavior]");
        b.AppendLine("# Safety fallback: DONE не должен мигать бесконечно даже если toast correlation не сработал.");
        b.AppendLine("# 0 = отключить fallback timeout; рекомендуется 15..60 секунд.");
        b.AppendLine($"done_attention_timeout_seconds = {Format(config.DoneAttentionTimeoutSeconds)}");
        b.AppendLine();
        WriteProfile(b, "A", config.Profiles.A, "RED / TOOLS-AUTH");
        WriteProfile(b, "B", config.Profiles.B, "BLUE / MAIN-VIBECODING");
        WriteEffect(b, "states.running", config.States.Running,
            "RUNNING: Flowing Water в цвете активного профиля.");
        WriteEffect(b, "states.waiting", config.States.Waiting,
            "WAITING: быстрое Single-color breathing, speed 7.");
        WriteEffect(b, "states.done", config.States.Done,
            "DONE_PENDING_ATTENTION: Single-color breathing speed 5; semantic timeout задаётся в [behavior].");
        WriteEffect(b, "states.error", config.States.Error,
            "ERROR зарезервирован для high-confidence semantic source; default disabled.");
        WriteEffect(b, "profile_switch", config.ProfileSwitch,
            "Flowing Water после физического переключения. У K15 есть собственная тройная flash-анимация; duration 4s оставляет наш сигнал видимым после неё.");
        WriteEffect(b, "stop_signal", config.StopSignal,
            "Момент STOP: короткий Cycle breathing RED <-> BLUE, затем states.done.");
        WriteEffect(b, "activation", config.ActivationSignal,
            "Включение RGB-индикации: короткий Flowing Water двумя основными цветами A/B.");
        b.AppendLine("[effect_lab]");
        b.AppendLine("# Встроенный tray Effect Test остаётся коротким smoke. Полные исследования делаются в Lighting Lab.");
        b.AppendLine($"test_duration_seconds = {Format(config.EffectLabDurationSeconds)}");
        return b.ToString();
    }

    private static void Apply(StatusLabConfig config, string section, string key, string value)
    {
        if (section.Length == 0)
        {
            if (key != "schema_version")
                throw new InvalidDataException($"unknown root key '{key}'");
            config.SchemaVersion = ParseInt(value);
            return;
        }

        if (section == "device")
        {
            if (key != "wire_color_order")
                throw new InvalidDataException($"unknown key '{section}.{key}'");
            config.WireColorOrder = ParseWireOrder(ParseString(value));
            return;
        }

        if (section == "behavior")
        {
            if (key != "done_attention_timeout_seconds")
                throw new InvalidDataException($"unknown key '{section}.{key}'");
            config.DoneAttentionTimeoutSeconds = ParseDouble(value);
            return;
        }

        if (section is "profiles.A" or "profiles.B")
        {
            if (key != "color")
                throw new InvalidDataException($"unknown key '{section}.{key}'");
            (section.EndsWith(".A", StringComparison.Ordinal) ? config.Profiles.A : config.Profiles.B).Color = ParseString(value);
            return;
        }

        if (section == "effect_lab")
        {
            if (key != "test_duration_seconds")
                throw new InvalidDataException($"unknown key '{section}.{key}'");
            config.EffectLabDurationSeconds = ParseDouble(value);
            return;
        }

        var effect = section switch
        {
            "states.running" => config.States.Running,
            "states.waiting" => config.States.Waiting,
            "states.done" => config.States.Done,
            "states.error" => config.States.Error,
            "profile_switch" => config.ProfileSwitch,
            "stop_signal" => config.StopSignal,
            "activation" => config.ActivationSignal,
            _ => throw new InvalidDataException($"unknown section [{section}]")
        };

        switch (key)
        {
            case "enabled": effect.Enabled = ParseBool(value); break;
            case "effect": effect.Mode = StatusLabConfig.ParseModeName(ParseString(value)); break;
            case "palette": effect.Palette = StatusLabConfig.ParsePaletteName(ParseString(value)); break;
            case "brightness": effect.Brightness = ParseInt(value); break;
            case "speed": effect.Speed = ParseInt(value); break;
            case "direction": effect.Direction = ParseInt(value); break;
            case "duration_seconds": effect.DurationSeconds = ParseDouble(value); break;
            default: throw new InvalidDataException($"unknown key '{section}.{key}'");
        }
    }

    private static void WriteProfile(StringBuilder b, string name, ProfileLightingConfig profile, string hint)
    {
        b.AppendLine($"[profiles.{name}]");
        b.AppendLine($"# {hint}");
        b.AppendLine($"color = \"{profile.Color}\"");
        b.AppendLine();
    }

    private static void WriteEffect(StringBuilder b, string section, LightingEffectConfig effect, string hint)
    {
        b.AppendLine($"[{section}]");
        b.AppendLine($"# {hint}");
        b.AppendLine($"enabled = {effect.Enabled.ToString().ToLowerInvariant()}");
        b.AppendLine($"effect = \"{StatusLabConfig.ModeName(effect.Mode)}\"");
        b.AppendLine($"palette = \"{StatusLabConfig.PaletteName(effect.Palette)}\"");
        b.AppendLine($"brightness = {effect.Brightness}      # 1..6");
        b.AppendLine($"speed = {effect.Speed}               # 1..7");
        b.AppendLine($"direction = {effect.Direction}           # 0..1");
        b.AppendLine($"duration_seconds = {Format(effect.DurationSeconds)}");
        b.AppendLine();
    }

    private static string StripComment(string line)
    {
        var quoted = false;
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && quoted) { escaped = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (c == '#' && !quoted) return line[..i];
        }
        return line;
    }

    private static string ParseString(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            throw new FormatException("string values must use double quotes");
        return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private static bool ParseBool(string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new FormatException($"invalid boolean '{value}'")
    };

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new FormatException($"invalid integer '{value}'");

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new FormatException($"invalid number '{value}'");

    private static WireColorOrder ParseWireOrder(string value) => value.ToLowerInvariant() switch
    {
        "rgb" => WireColorOrder.RGB,
        "grb" => WireColorOrder.GRB,
        _ => throw new FormatException("wire_color_order must be rgb or grb")
    };

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static void Fail(int zeroBasedLine, string message) =>
        throw new InvalidDataException($"config.toml line {zeroBasedLine + 1}: {message}");
}
