using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Vorotex.K15.StatusLab;

internal enum BuildChannel
{
    Unknown,
    Stable,
    Canary
}

internal sealed record BuildProvenance(
    BuildChannel Channel,
    string Version,
    string Commit,
    string? CanaryIssue,
    string? CanaryPullRequest,
    DateTimeOffset? BuiltUtc,
    string? SourceRef)
{
    private const int MaxSourceRefLength = 120;
    private static readonly Regex SemanticVersionPattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IdentifierPattern = new(@"^[1-9]\d{0,8}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SourceRefPattern = new(@"^[A-Za-z0-9._/-]{1,120}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> KnownMetadataKeys = new(StringComparer.Ordinal)
    {
        "Version", "Commit", "Channel", "CanaryIssue", "CanaryPullRequest", "BuildUtc", "SourceRef"
    };

    public bool IsKnown => Channel != BuildChannel.Unknown;
    public string ChannelLabel => Channel switch
    {
        BuildChannel.Stable => "STABLE",
        BuildChannel.Canary => "CANARY",
        _ => "UNKNOWN BUILD"
    };

    public string CompactVersion => IsKnown ? $"v{Version}" : "UNKNOWN BUILD";
    public string ShortCommit => IsKnown ? Commit[..7] : "неизвестен";
    public string ChannelDisplay => Channel switch
    {
        BuildChannel.Canary when CanaryIssue is not null => $"CANARY #{CanaryIssue}",
        BuildChannel.Canary when CanaryPullRequest is not null => $"CANARY PR #{CanaryPullRequest}",
        _ => ChannelLabel
    };
    public string SourceDisplay
    {
        get
        {
            if (!IsKnown) return "неизвестен";
            var parts = new List<string>();
            if (SourceRef is not null) parts.Add(SourceRef);
            if (CanaryIssue is not null) parts.Add($"Issue #{CanaryIssue}");
            if (CanaryPullRequest is not null) parts.Add($"PR #{CanaryPullRequest}");
            return parts.Count == 0 ? "не указан" : string.Join(" · ", parts);
        }
    }

    public static BuildProvenance Unknown { get; } = new(
        BuildChannel.Unknown, "UNKNOWN", string.Empty, null, null, null, null);

    public static BuildProvenance FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return FromMetadataAttributes(assembly.GetCustomAttributes<AssemblyMetadataAttribute>());
    }

    public static BuildProvenance FromMetadataAttributes(IEnumerable<AssemblyMetadataAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var selected = attributes.Where(attribute => attribute.Key.StartsWith("K15.Build.", StringComparison.Ordinal)).ToArray();
        if (selected.GroupBy(attribute => attribute.Key, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return Unknown;
        var values = selected.ToDictionary(
            attribute => attribute.Key["K15.Build.".Length..],
            attribute => attribute.Value,
            StringComparer.Ordinal);
        return Parse(values);
    }

    public static BuildProvenance Parse(IReadOnlyDictionary<string, string?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Keys.Any(key => !KnownMetadataKeys.Contains(key)))
            return Unknown;
        string? Get(string key) => metadata.TryGetValue(key, out var value) ? value : null;

        var version = Get("Version");
        var commit = Get("Commit");
        var channelText = Get("Channel");
        var issue = NormalizeOptionalIdentifier(Get("CanaryIssue"));
        var pullRequest = NormalizeOptionalIdentifier(Get("CanaryPullRequest"));
        var buildUtcText = Get("BuildUtc");
        var sourceRef = NormalizeOptionalSourceRef(Get("SourceRef"));

        if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || !SemanticVersionPattern.IsMatch(version) ||
            commit is null || !Regex.IsMatch(commit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(channelText) ||
            !DateTimeOffset.TryParseExact(
                buildUtcText,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var builtUtc) ||
            !TryParseChannel(channelText, out var channel) ||
            !OptionalIdentifierIsValid(Get("CanaryIssue"), issue) ||
            !OptionalIdentifierIsValid(Get("CanaryPullRequest"), pullRequest) ||
            !OptionalSourceRefIsValid(Get("SourceRef"), sourceRef) ||
            (channel == BuildChannel.Stable && (issue is not null || pullRequest is not null)) ||
            (channel == BuildChannel.Canary && issue is null && pullRequest is null))
        {
            return Unknown;
        }

        return new BuildProvenance(channel, version, commit, issue, pullRequest, builtUtc, sourceRef);
    }

    public string FormatSummary()
    {
        if (!IsKnown)
            return "Канал: UNKNOWN BUILD\r\nВерсия: неизвестна\r\nCommit: неизвестен\r\nСборка UTC: неизвестно";

        var lines = new List<string>
        {
            $"Канал: {ChannelLabel}",
            $"Версия: {Version}",
            $"Commit: {Commit}",
            $"Источник: {SourceDisplay}",
            $"Сборка UTC: {BuiltUtc!.Value:yyyy-MM-dd HH:mm:ss} UTC"
        };
        return string.Join("\r\n", lines);
    }

    public string FormatTooltip(string state, bool rgbEnabled)
    {
        var rgb = rgbEnabled ? "RGB ON" : "RGB OFF";
        var safeState = string.IsNullOrWhiteSpace(state) ? "UNKNOWN" : state.Trim();
        var value = IsKnown
            ? $"K15 {ChannelDisplay} · {safeState} · {(Version.Length <= 18 ? $"v{Version} · " : string.Empty)}{ShortCommit} · {rgb}"
            : $"K15 UNKNOWN BUILD · {safeState} · {rgb}";
        return value.Length <= 63 ? value : value[..62] + "…";
    }

    private static bool TryParseChannel(string? value, out BuildChannel channel)
    {
        if (string.Equals(value?.Trim(), "stable", StringComparison.OrdinalIgnoreCase))
        {
            channel = BuildChannel.Stable;
            return true;
        }
        if (string.Equals(value?.Trim(), "canary", StringComparison.OrdinalIgnoreCase))
        {
            channel = BuildChannel.Canary;
            return true;
        }
        channel = BuildChannel.Unknown;
        return false;
    }

    private static string? NormalizeOptionalIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool OptionalIdentifierIsValid(string? original, string? normalized) =>
        string.IsNullOrWhiteSpace(original) || (normalized is not null && IdentifierPattern.IsMatch(normalized));

    private static string? NormalizeOptionalSourceRef(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool OptionalSourceRefIsValid(string? original, string? normalized) =>
        string.IsNullOrWhiteSpace(original) || (normalized is not null && SourceRefPattern.IsMatch(normalized) && normalized.Length <= MaxSourceRefLength);
}
