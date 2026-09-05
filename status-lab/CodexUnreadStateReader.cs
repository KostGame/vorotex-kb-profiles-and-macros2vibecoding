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
            if (!UniqueProperty(doc.RootElement, "electron-persisted-atom-state", out var atoms, out var missing))
                return Failed(missing ? CodexUnreadState.Unavailable : CodexUnreadState.Unknown);
            if (!UniqueProperty(atoms, "unread-thread-ids-by-host-v1", out var hosts, out missing))
                return Failed(missing ? CodexUnreadState.Unavailable : CodexUnreadState.Unknown);
            if (hosts.ValueKind != JsonValueKind.Object) return Failed(CodexUnreadState.Unknown);
            var hostNames = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string>? selected = null;
            var count = 0;
            foreach (var partition in hosts.EnumerateObject())
            {
                if (!Bounded(partition.Name, 256) || !hostNames.Add(partition.Name) || hostNames.Count > MaxHosts ||
                    partition.Value.ValueKind != JsonValueKind.Array) return Failed(CodexUnreadState.Unknown);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in partition.Value.EnumerateArray())
                {
                    if (++count > MaxIds || entry.ValueKind != JsonValueKind.String ||
                        !Bounded(entry.GetString(), 1024) || !ids.Add(entry.GetString()!)) return Failed(CodexUnreadState.Unknown);
                }
                if (partition.Name == host) selected = ids;
            }
            return selected is null ? Failed(CodexUnreadState.Unknown) :
                new(host, startedUtc, finishedUtc, selected, CodexUnreadState.Unknown);
        }
        catch (JsonException) { return Failed(CodexUnreadState.Unknown); }
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
