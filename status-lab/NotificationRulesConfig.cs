using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vorotex.K15.StatusLab;

internal sealed class NotificationRulesConfig
{
    public const int CurrentSchemaVersion = 1;
    public static string FilePath { get; } = Path.Combine(EventJournal.DirectoryPath, "notifications.toml");

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool Enabled { get; set; } = true;
    public int LearningBufferSize { get; set; } = 50;
    public string DefaultAction { get; set; } = "ignore";
    public List<NotificationRule> Rules { get; set; } = [];
    public string? LoadWarning { get; private set; }

    public static NotificationRulesConfig CreateDefault() => new();

    public static NotificationRulesConfig LoadOrCreate()
    {
        EnsureExists();
        try
        {
            return NotificationRulesToml.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            var fallback = CreateDefault();
            fallback.LoadWarning =
                $"Notification rules invalid: {ex.Message}. Existing notifications.toml was preserved unchanged; rules are disabled for this run.";
            fallback.Enabled = false;
            return fallback;
        }
    }

    public static void EnsureExists()
    {
        Directory.CreateDirectory(EventJournal.DirectoryPath);
        if (File.Exists(FilePath))
            return;
        File.WriteAllText(FilePath, NotificationRulesToml.Serialize(CreateDefault()), new UTF8Encoding(false));
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported notification schema_version {SchemaVersion}; expected {CurrentSchemaVersion}.");
        if (LearningBufferSize is < 1 or > 500)
            throw new InvalidDataException("notifications.learning_buffer_size must be 1..500.");
        if (!DefaultAction.Equals("ignore", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("notifications.default_action currently supports only 'ignore'.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new InvalidDataException("Each notification rule requires a non-empty id.");
            if (!ids.Add(rule.Id.Trim()))
                throw new InvalidDataException($"Duplicate notification rule id '{rule.Id}'.");
            if (!rule.Match.HasAnyCondition)
                throw new InvalidDataException($"Rule '{rule.Id}' must define at least one match condition.");
            if (rule.MaxDurationSeconds is < 1 or > 3600)
                throw new InvalidDataException($"Rule '{rule.Id}'.max_duration_seconds must be 1..3600.");

            ValidateDisplay(rule.Id, rule.Display);
            ValidateRegex(rule.Id, rule.Match.Regex);
        }
    }

    private static void ValidateDisplay(string ruleId, NotificationVisualConfig display)
    {
        var effect = display.Effect.Trim().ToLowerInvariant();
        if (effect is not ("constant" or "flowing_water" or "single_color_breathing" or "cycle_breathing" or "off"))
            throw new InvalidDataException($"Rule '{ruleId}' uses unsupported notification effect '{display.Effect}'.");
        if (!Regex.IsMatch(display.Color, "^#[0-9a-fA-F]{6}$"))
            throw new InvalidDataException($"Rule '{ruleId}'.display.color must be #RRGGBB.");
        if (display.Brightness is < 1 or > 6)
            throw new InvalidDataException($"Rule '{ruleId}'.display.brightness must be 1..6.");
        if (display.Speed is < 1 or > 7)
            throw new InvalidDataException($"Rule '{ruleId}'.display.speed must be 1..7.");
        if (display.Direction is < 0 or > 1)
            throw new InvalidDataException($"Rule '{ruleId}'.display.direction must be 0 or 1.");
        if (display.DurationSeconds is < 0.5 or > 300)
            throw new InvalidDataException($"Rule '{ruleId}'.display.duration_seconds must be 0.5..300.");
    }

    private static void ValidateRegex(string ruleId, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return;
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Rule '{ruleId}'.match.regex is invalid: {ex.Message}");
        }
    }
}

internal static class NotificationRulesToml
{
    private enum Section
    {
        Root,
        Notifications,
        Rule,
        RuleMatch,
        RuleDisplay
    }

    public static NotificationRulesConfig Parse(string text)
    {
        var config = NotificationRulesConfig.CreateDefault();
        var section = Section.Root;
        NotificationRule? currentRule = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = StripComment(lines[index]).Trim();
            if (raw.Length == 0)
                continue;

            if (raw.Equals("[[rules]]", StringComparison.Ordinal))
            {
                currentRule = new NotificationRule();
                config.Rules.Add(currentRule);
                section = Section.Rule;
                continue;
            }

            if (raw.StartsWith('[') && raw.EndsWith(']'))
            {
                section = raw switch
                {
                    "[notifications]" => Section.Notifications,
                    "[rules.match]" when currentRule is not null => Section.RuleMatch,
                    "[rules.display]" when currentRule is not null => Section.RuleDisplay,
                    _ => throw LineError(index, $"unknown or misplaced section {raw}")
                };
                continue;
            }

            var equals = raw.IndexOf('=');
            if (equals <= 0)
                throw LineError(index, "expected key = value");

            var key = raw[..equals].Trim();
            var value = raw[(equals + 1)..].Trim();
            try
            {
                Apply(config, currentRule, section, key, value);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                throw LineError(index, ex.Message);
            }
        }

        config.Validate();
        return config;
    }

    public static string Serialize(NotificationRulesConfig config)
    {
        config.Validate();
        var b = new StringBuilder();
        b.AppendLine("# VOROTEX K15 Status Lab — Windows notification rules");
        b.AppendLine("# Arbitrary Windows notifications create temporary overlays only.");
        b.AppendLine("# They NEVER change Codex NORMAL/RUNNING/WAITING/DONE semantic state.");
        b.AppendLine("# Raw notification title/body are kept in memory for matching/learning and are not journaled by default.");
        b.AppendLine();
        b.AppendLine($"schema_version = {config.SchemaVersion}");
        b.AppendLine();
        b.AppendLine("[notifications]");
        b.AppendLine($"enabled = {Bool(config.Enabled)}");
        b.AppendLine($"learning_buffer_size = {config.LearningBufferSize}  # 1..500, RAM only");
        b.AppendLine($"default_action = \"{Escape(config.DefaultAction)}\"  # currently: ignore");
        b.AppendLine();
        b.AppendLine("# Example rule (uncomment/copy and replace app identity after Learning Mode discovers it):");
        b.AppendLine("# [[rules]]");
        b.AppendLine("# id = \"telegram\"");
        b.AppendLine("# enabled = true");
        b.AppendLine("# priority = \"normal\"       # low | normal | high | critical");
        b.AppendLine("# behavior = \"pulse\"        # pulse | while_present | until_acknowledged");
        b.AppendLine("# max_duration_seconds = 60");
        b.AppendLine("# [rules.match]");
        b.AppendLine("# package_family_name = \"...\"");
        b.AppendLine("# title_contains = []");
        b.AppendLine("# body_contains = []");
        b.AppendLine("# regex = \"\"");
        b.AppendLine("# [rules.display]");
        b.AppendLine("# effect = \"single_color_breathing\"");
        b.AppendLine("# color_mode = \"custom\"     # custom | custom_plus_profile");
        b.AppendLine("# color = \"#27A7E7\"");
        b.AppendLine("# brightness = 6");
        b.AppendLine("# speed = 7");
        b.AppendLine("# direction = 0");
        b.AppendLine("# duration_seconds = 6");

        foreach (var rule in config.Rules)
        {
            b.AppendLine();
            b.AppendLine("[[rules]]");
            b.AppendLine($"id = \"{Escape(rule.Id)}\"");
            b.AppendLine($"enabled = {Bool(rule.Enabled)}");
            b.AppendLine($"priority = \"{PriorityName(rule.Priority)}\"");
            b.AppendLine($"behavior = \"{BehaviorName(rule.Behavior)}\"");
            b.AppendLine($"max_duration_seconds = {Format(rule.MaxDurationSeconds)}");
            b.AppendLine();
            b.AppendLine("[rules.match]");
            b.AppendLine($"package_family_name = \"{Escape(rule.Match.PackageFamilyName)}\"");
            b.AppendLine($"app_user_model_id = \"{Escape(rule.Match.AppUserModelId)}\"");
            b.AppendLine($"app_name = \"{Escape(rule.Match.AppName)}\"");
            b.AppendLine($"title_contains = {Array(rule.Match.TitleContains)}");
            b.AppendLine($"body_contains = {Array(rule.Match.BodyContains)}");
            b.AppendLine($"regex = \"{Escape(rule.Match.Regex)}\"");
            b.AppendLine();
            b.AppendLine("[rules.display]");
            b.AppendLine($"effect = \"{Escape(rule.Display.Effect)}\"");
            b.AppendLine($"color_mode = \"{ColorModeName(rule.Display.ColorMode)}\"");
            b.AppendLine($"color = \"{Escape(rule.Display.Color)}\"");
            b.AppendLine($"brightness = {rule.Display.Brightness}");
            b.AppendLine($"speed = {rule.Display.Speed}");
            b.AppendLine($"direction = {rule.Display.Direction}");
            b.AppendLine($"duration_seconds = {Format(rule.Display.DurationSeconds)}");
        }

        return b.ToString();
    }

    private static void Apply(NotificationRulesConfig config, NotificationRule? rule, Section section, string key, string value)
    {
        if (section == Section.Root)
        {
            if (key != "schema_version")
                throw new InvalidDataException($"unknown root key '{key}'");
            config.SchemaVersion = ParseInt(value);
            return;
        }

        if (section == Section.Notifications)
        {
            switch (key)
            {
                case "enabled": config.Enabled = ParseBool(value); return;
                case "learning_buffer_size": config.LearningBufferSize = ParseInt(value); return;
                case "default_action": config.DefaultAction = ParseString(value); return;
                default: throw new InvalidDataException($"unknown notifications key '{key}'");
            }
        }

        if (rule is null)
            throw new InvalidDataException("rule key used before [[rules]]");

        if (section == Section.Rule)
        {
            switch (key)
            {
                case "id": rule.Id = ParseString(value); return;
                case "enabled": rule.Enabled = ParseBool(value); return;
                case "priority": rule.Priority = ParsePriority(ParseString(value)); return;
                case "behavior": rule.Behavior = ParseBehavior(ParseString(value)); return;
                case "max_duration_seconds": rule.MaxDurationSeconds = ParseDouble(value); return;
                default: throw new InvalidDataException($"unknown rule key '{key}'");
            }
        }

        if (section == Section.RuleMatch)
        {
            switch (key)
            {
                case "package_family_name": rule.Match.PackageFamilyName = ParseString(value); return;
                case "app_user_model_id": rule.Match.AppUserModelId = ParseString(value); return;
                case "app_name": rule.Match.AppName = ParseString(value); return;
                case "title_contains": rule.Match.TitleContains = ParseStringArray(value); return;
                case "body_contains": rule.Match.BodyContains = ParseStringArray(value); return;
                case "regex": rule.Match.Regex = ParseString(value); return;
                default: throw new InvalidDataException($"unknown rules.match key '{key}'");
            }
        }

        if (section == Section.RuleDisplay)
        {
            switch (key)
            {
                case "effect": rule.Display.Effect = ParseString(value); return;
                case "color_mode": rule.Display.ColorMode = ParseColorMode(ParseString(value)); return;
                case "color": rule.Display.Color = ParseString(value); return;
                case "brightness": rule.Display.Brightness = ParseInt(value); return;
                case "speed": rule.Display.Speed = ParseInt(value); return;
                case "direction": rule.Display.Direction = ParseInt(value); return;
                case "duration_seconds": rule.Display.DurationSeconds = ParseDouble(value); return;
                default: throw new InvalidDataException($"unknown rules.display key '{key}'");
            }
        }

        throw new InvalidDataException($"key '{key}' is not valid in the current section");
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

    private static string[] ParseStringArray(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith('[') || !text.EndsWith(']'))
            throw new FormatException("array values must use [\"a\", \"b\"]");
        text = text[1..^1].Trim();
        if (text.Length == 0)
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var c in text)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\' && quoted)
            {
                current.Append(c);
                escaped = true;
                continue;
            }
            if (c == '"')
            {
                current.Append(c);
                quoted = !quoted;
                continue;
            }
            if (c == ',' && !quoted)
            {
                result.Add(ParseString(current.ToString().Trim()));
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (quoted)
            throw new FormatException("unterminated string array value");
        result.Add(ParseString(current.ToString().Trim()));
        return result.ToArray();
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

    private static NotificationPriority ParsePriority(string value) => value.Trim().ToLowerInvariant() switch
    {
        "low" => NotificationPriority.Low,
        "normal" => NotificationPriority.Normal,
        "high" => NotificationPriority.High,
        "critical" => NotificationPriority.Critical,
        _ => throw new FormatException("priority must be low, normal, high or critical")
    };

    private static NotificationBehavior ParseBehavior(string value) => value.Trim().ToLowerInvariant() switch
    {
        "pulse" => NotificationBehavior.Pulse,
        "while_present" => NotificationBehavior.WhilePresent,
        "until_acknowledged" => NotificationBehavior.UntilAcknowledged,
        _ => throw new FormatException("behavior must be pulse, while_present or until_acknowledged")
    };

    private static NotificationColorMode ParseColorMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "custom" => NotificationColorMode.Custom,
        "custom_plus_profile" => NotificationColorMode.CustomPlusProfile,
        _ => throw new FormatException("color_mode must be custom or custom_plus_profile")
    };

    private static string PriorityName(NotificationPriority value) => value.ToString().ToLowerInvariant();
    private static string BehaviorName(NotificationBehavior value) => value switch
    {
        NotificationBehavior.Pulse => "pulse",
        NotificationBehavior.WhilePresent => "while_present",
        NotificationBehavior.UntilAcknowledged => "until_acknowledged",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static string ColorModeName(NotificationColorMode value) => value switch
    {
        NotificationColorMode.Custom => "custom",
        NotificationColorMode.CustomPlusProfile => "custom_plus_profile",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string Array(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(value => $"\"{Escape(value)}\"")) + "]";
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static InvalidDataException LineError(int zeroBasedLine, string message) =>
        new($"notifications.toml line {zeroBasedLine + 1}: {message}");
}
