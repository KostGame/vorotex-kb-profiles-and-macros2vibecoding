using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal enum CodexUnreadState { Unknown, Unavailable, HasUnread, NoUnread }

internal sealed record CodexUnreadSnapshot(string Host, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, IReadOnlySet<string>? ThreadIds, CodexUnreadState Failure)
{
    public CodexUnreadState ForThread(string threadId) => ThreadIds is null ? Failure :
        ThreadIds.Contains(threadId) ? CodexUnreadState.HasUnread : CodexUnreadState.NoUnread;
}

internal interface ICodexUnreadStateReader
{
    CodexUnreadSnapshot Read(DateTimeOffset startedUtc);
}

// The file is an observation source only. Never write it or interpret a missing
// host as an empty unread list. No unrelated state atoms escape this reader.
internal sealed class CodexUnreadStateReader(string? path, string host) : ICodexUnreadStateReader
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    internal const int MaxIds = 10000;
    internal const int MaxHosts = 256;
    internal const int MaxIdentities = 16;

    internal static string? ResolveStatePath(string? codexHome)
    {
        // An explicit home avoids silently observing a stale installation/profile.
        if (string.IsNullOrWhiteSpace(codexHome) || !Path.IsPathFullyQualified(codexHome)) return null;
        try { return Path.Combine(Path.GetFullPath(codexHome), ".codex-global-state.json"); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    public CodexUnreadSnapshot Read(DateTimeOffset startedUtc)
    {
        CodexUnreadSnapshot Failed(CodexUnreadState state) => new(host, startedUtc, DateTimeOffset.UtcNow, null, state);
        if (path is null || !Bounded(host, 256)) return Failed(CodexUnreadState.Unavailable);
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length < 1 || length > MaxBytes) return Failed(CodexUnreadState.Unknown);
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1 || stream.Length != length || File.GetLastWriteTimeUtc(path) != stamp)
                return Failed(CodexUnreadState.Unknown);
            return Parse(bytes, host, startedUtc, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Failed(CodexUnreadState.Unavailable); }
    }

    internal static CodexUnreadSnapshot Parse(ReadOnlyMemory<byte> bytes, string host,
        DateTimeOffset startedUtc, DateTimeOffset finishedUtc)
    {
        CodexUnreadSnapshot Failed(CodexUnreadState state) => new(host, startedUtc, finishedUtc, null, state);
        if (bytes.Length > MaxBytes || !Bounded(host, 256)) return Failed(CodexUnreadState.Unknown);
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (!UniqueProperty(doc.RootElement, "electron-thread-read-state-v1", out var canonical, out var missing))
                return missing
                    ? ParseLegacy(doc.RootElement, host, startedUtc, finishedUtc)
                    : Failed(CodexUnreadState.Unknown);
            return ParseCanonical(canonical, host, startedUtc, finishedUtc);
        }
        catch (JsonException) { return Failed(CodexUnreadState.Unknown); }
    }

    private static CodexUnreadSnapshot ParseLegacy(JsonElement root, string host,
        DateTimeOffset startedUtc, DateTimeOffset finishedUtc)
    {
        CodexUnreadSnapshot Failed(CodexUnreadState state) => new(host, startedUtc, finishedUtc, null, state);
        if (!UniqueProperty(root, "electron-persisted-atom-state", out var atoms, out var missing))
            return Failed(missing ? CodexUnreadState.Unavailable : CodexUnreadState.Unknown);
        if (!UniqueProperty(atoms, "unread-thread-ids-by-host-v1", out var hosts, out missing))
            return Failed(missing ? CodexUnreadState.Unavailable : CodexUnreadState.Unknown);
        return ParseHostPartitions(hosts, host, startedUtc, finishedUtc, Failed);
    }

    private static CodexUnreadSnapshot ParseCanonical(JsonElement canonical, string host,
        DateTimeOffset startedUtc, DateTimeOffset finishedUtc)
    {
        CodexUnreadSnapshot Failed(CodexUnreadState state) => new(host, startedUtc, finishedUtc, null, state);
        if (canonical.ValueKind != JsonValueKind.Object ||
            !UniqueProperty(canonical, "version", out var version, out _) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionValue) || versionValue != 1 ||
            !UniqueProperty(canonical, "unreadByIdentity", out var identities, out _))
            return Failed(CodexUnreadState.Unknown);
        if (identities.ValueKind != JsonValueKind.Object)
            return Failed(CodexUnreadState.Unknown);

        var identityPartitions = identities.EnumerateObject().ToArray();
        if (identityPartitions.Any(item => !Bounded(item.Name, 256)) ||
            identityPartitions.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != identityPartitions.Length)
            return Failed(CodexUnreadState.Unknown);
        if (identityPartitions.Length == 0 || identityPartitions.Length > MaxIdentities)
            return Failed(CodexUnreadState.Unknown);
        JsonElement selectedIdentity;
        if (identityPartitions.Length != 1)
            return Failed(CodexUnreadState.Unknown);
        selectedIdentity = identityPartitions[0].Value;
        if (selectedIdentity.ValueKind != JsonValueKind.Object)
            return Failed(CodexUnreadState.Unknown);

        var hostPartitions = selectedIdentity.EnumerateObject().ToArray();
        if (hostPartitions.Any(item => !Bounded(item.Name, 256)) ||
            hostPartitions.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != hostPartitions.Length)
            return Failed(CodexUnreadState.Unknown);
        if (hostPartitions.Length == 0 || hostPartitions.Length > MaxHosts)
            return Failed(CodexUnreadState.Unknown);
        var selectedHost = hostPartitions.Length == 1 ? hostPartitions[0].Name :
            AdoptedLocalHost(canonical, hostPartitions.Select(item => item.Name).ToArray());
        if (selectedHost is null)
            return Failed(CodexUnreadState.Unknown);
        var partition = hostPartitions.SingleOrDefault(item => item.Name == selectedHost);
        return partition.Name is null
            ? Failed(CodexUnreadState.Unknown)
            : ParseIds(partition.Value, host, startedUtc, finishedUtc, Failed);
    }

    private static string? AdoptedLocalHost(JsonElement canonical, string[] hosts)
    {
        if (!UniqueProperty(canonical, "legacyMigration", out var migration, out _) || migration.ValueKind != JsonValueKind.Object ||
            !UniqueProperty(migration, "adoptedHostIds", out var adopted, out _) || adopted.ValueKind != JsonValueKind.Object ||
            !UniqueProperty(adopted, "local", out var local, out _) || local.ValueKind != JsonValueKind.String)
            return null;
        var value = local.GetString();
        return value is not null && hosts.Contains(value, StringComparer.Ordinal) ? value : null;
    }

    private static CodexUnreadSnapshot ParseHostPartitions(JsonElement hosts, string host,
        DateTimeOffset startedUtc, DateTimeOffset finishedUtc, Func<CodexUnreadState, CodexUnreadSnapshot> failed)
    {
        if (hosts.ValueKind != JsonValueKind.Object) return failed(CodexUnreadState.Unknown);
        var hostNames = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? selected = null;
        var count = 0;
        foreach (var partition in hosts.EnumerateObject())
        {
            if (!Bounded(partition.Name, 256) || !hostNames.Add(partition.Name) || hostNames.Count > MaxHosts ||
                partition.Value.ValueKind != JsonValueKind.Array) return failed(CodexUnreadState.Unknown);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in partition.Value.EnumerateArray())
            {
                if (++count > MaxIds || entry.ValueKind != JsonValueKind.String ||
                    !Bounded(entry.GetString(), 1024) || !ids.Add(entry.GetString()!)) return failed(CodexUnreadState.Unknown);
            }
            if (partition.Name == host) selected = ids;
        }
        return selected is null ? failed(CodexUnreadState.Unknown) :
            new(host, startedUtc, finishedUtc, selected, CodexUnreadState.Unknown);
    }

    private static CodexUnreadSnapshot ParseIds(JsonElement value, string host,
        DateTimeOffset startedUtc, DateTimeOffset finishedUtc, Func<CodexUnreadState, CodexUnreadSnapshot> failed)
    {
        if (value.ValueKind != JsonValueKind.Array) return failed(CodexUnreadState.Unknown);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateArray())
        {
            if (ids.Count >= MaxIds || entry.ValueKind != JsonValueKind.String ||
                !Bounded(entry.GetString(), 1024) || !ids.Add(entry.GetString()!))
                return failed(CodexUnreadState.Unknown);
        }
        return new(host, startedUtc, finishedUtc, ids, CodexUnreadState.Unknown);
    }

    private static bool UniqueProperty(JsonElement parent, string name, out JsonElement value, out bool missing)
    {
        value = default;
        missing = false;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        var count = 0;
        foreach (var item in parent.EnumerateObject())
            if (item.Name == name) { value = item.Value; count++; }
        missing = count == 0;
        return count == 1;
    }

    internal static bool Bounded(string? value, int maxBytes = 128) => !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl) && Encoding.UTF8.GetByteCount(value) <= maxBytes;
}
