using System.Text.RegularExpressions;

namespace Vorotex.K15.StatusLab;

internal enum NotificationPriority
{
    Low = 10,
    Normal = 20,
    High = 30,
    Critical = 40
}

internal enum NotificationBehavior
{
    Pulse,
    WhilePresent,
    UntilAcknowledged
}

internal enum NotificationColorMode
{
    Custom,
    CustomPlusProfile
}

internal sealed class NotificationRuleMatch
{
    public string PackageFamilyName { get; set; } = string.Empty;
    public string AppUserModelId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string[] TitleContains { get; set; } = [];
    public string[] BodyContains { get; set; } = [];
    public string Regex { get; set; } = string.Empty;

    public bool HasAnyCondition =>
        !string.IsNullOrWhiteSpace(PackageFamilyName) ||
        !string.IsNullOrWhiteSpace(AppUserModelId) ||
        !string.IsNullOrWhiteSpace(AppName) ||
        TitleContains.Length > 0 ||
        BodyContains.Length > 0 ||
        !string.IsNullOrWhiteSpace(Regex);
}

internal sealed class NotificationVisualConfig
{
    public string Effect { get; set; } = "single_color_breathing";
    public NotificationColorMode ColorMode { get; set; } = NotificationColorMode.Custom;
    public string Color { get; set; } = "#FFFFFF";
    public int Brightness { get; set; } = 6;
    public int Speed { get; set; } = 7;
    public int Direction { get; set; }
    public double DurationSeconds { get; set; } = 6;
}

internal sealed class NotificationRule
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
    public NotificationBehavior Behavior { get; set; } = NotificationBehavior.Pulse;
    public double MaxDurationSeconds { get; set; } = 60;
    public NotificationRuleMatch Match { get; set; } = new();
    public NotificationVisualConfig Display { get; set; } = new();
}

internal sealed record NotificationOverlayIntent(
    string NotificationKey,
    uint NotificationId,
    string RuleId,
    NotificationPriority Priority,
    NotificationBehavior Behavior,
    WindowsNotificationChangeKind ChangeKind,
    bool Dismiss,
    NotificationVisualConfig Display,
    DateTimeOffset SourceCreatedUtc);

internal sealed class NotificationRuleEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private readonly NotificationRule[] _rules;

    public NotificationRuleEngine(IEnumerable<NotificationRule> rules)
    {
        _rules = rules
            .Select((rule, index) => (rule, index))
            .OrderByDescending(item => item.rule.Priority)
            .ThenBy(item => item.index)
            .Select(item => item.rule)
            .ToArray();
    }

    public NotificationOverlayIntent? Evaluate(WindowsNotificationObservation observation)
    {
        if (observation.ChangeKind == WindowsNotificationChangeKind.Present)
            return null;

        var rule = _rules.FirstOrDefault(candidate => candidate.Enabled && Matches(candidate.Match, observation));
        if (rule is null)
            return null;

        if (observation.ChangeKind == WindowsNotificationChangeKind.Removed &&
            rule.Behavior == NotificationBehavior.Pulse)
        {
            return null;
        }

        return new NotificationOverlayIntent(
            observation.Key,
            observation.NotificationId,
            rule.Id,
            rule.Priority,
            rule.Behavior,
            observation.ChangeKind,
            observation.ChangeKind == WindowsNotificationChangeKind.Removed,
            CloneDisplay(rule.Display),
            observation.CreationTime.ToUniversalTime());
    }

    internal static bool Matches(NotificationRuleMatch match, WindowsNotificationObservation observation)
    {
        if (!match.HasAnyCondition)
            return false;

        if (!ExactOrEmpty(match.PackageFamilyName, observation.PackageFamilyName) ||
            !ExactOrEmpty(match.AppUserModelId, observation.AppUserModelId) ||
            !ExactOrEmpty(match.AppName, observation.AppName))
        {
            return false;
        }

        if (!ContainsAnyOrEmpty(observation.Title, match.TitleContains) ||
            !ContainsAnyOrEmpty(observation.Body, match.BodyContains))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(match.Regex))
        {
            try
            {
                if (!Regex.IsMatch(
                        $"{observation.Title}\n{observation.Body}",
                        match.Regex,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        RegexTimeout))
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ExactOrEmpty(string expected, string actual) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAnyOrEmpty(string value, string[] terms)
    {
        var active = terms.Where(term => !string.IsNullOrWhiteSpace(term)).ToArray();
        return active.Length == 0 ||
               active.Any(term => value.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static NotificationVisualConfig CloneDisplay(NotificationVisualConfig source) => new()
    {
        Effect = source.Effect,
        ColorMode = source.ColorMode,
        Color = source.Color,
        Brightness = source.Brightness,
        Speed = source.Speed,
        Direction = source.Direction,
        DurationSeconds = source.DurationSeconds
    };
}
