using System.Security.Cryptography;
using System.Text;

namespace Vorotex.K15.StatusLab;

// Local source identity contract shared by hook health, journal routing, and
// unread routing. Raw home paths never leave this process boundary.
internal static class CodexSourceIdentity
{
    internal const string Prefix = "local:";
    internal const int HexLength = 32;
    internal const int MaxBytes = 64;
    private const string HashDomain = "codex-home/v1\0";

    internal static string? ForHome(string? home)
    {
        var normalized = CanonicalizeHome(home);
        if (normalized is null) return null;

        var input = Encoding.UTF8.GetBytes(HashDomain + normalized);
        var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return Prefix + hash[..HexLength];
    }

    internal static string? CanonicalizeHome(string? home)
    {
        if (string.IsNullOrWhiteSpace(home)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(home);
            var full = Path.GetFullPath(expanded);
            var root = Path.GetPathRoot(full);
            var normalized = root is not null && string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? root
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(normalized)) return null;
            return normalized.Replace('/', '\\').ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > MaxBytes ||
            value.Length != Prefix.Length + HexLength || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        return value[Prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    internal static string CompositeKey(string sourceInstanceId, string identity) =>
        sourceInstanceId + "\u001f" + identity;
}

internal static class CodexHomeDiscovery
{
    internal static IReadOnlyList<string> DetectHomes()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfPresent(string? value)
        {
            var canonical = CodexSourceIdentity.CanonicalizeHome(value);
            if (canonical is not null && Directory.Exists(canonical) && seen.Add(canonical))
                result.Add(canonical);
        }

        AddIfPresent(Environment.GetEnvironmentVariable("CODEX_HOME"));
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddIfPresent(Path.Combine(user, ".codex-agentloop"));
        AddIfPresent(Path.Combine(user, ".codex"));
        try
        {
            foreach (var path in Directory.EnumerateDirectories(user, ".codex-*", SearchOption.TopDirectoryOnly))
                AddIfPresent(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }
}
