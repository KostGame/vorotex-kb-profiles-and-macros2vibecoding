namespace Vorotex.K15.StatusLab;

internal enum CodexUnreadSourceHealth
{
    Configured,
    Unavailable,
    Duplicate
}

internal sealed record CodexUnreadSource(
    string SourceInstanceId,
    string CodexHomePath,
    string? StatePath,
    string HostPartition,
    CodexUnreadSourceHealth Health,
    string Detail);

internal interface ICodexUnreadSourceRegistry
{
    int SourceCount { get; }
    IReadOnlyList<CodexUnreadSource> Sources { get; }
    CodexUnreadSnapshot Read(string sourceInstanceId, DateTimeOffset startedUtc);
}

internal sealed class CodexUnreadSourceRegistry : ICodexUnreadSourceRegistry
{
    private const string HostPartition = "local";
    private readonly IReadOnlyList<CodexUnreadSource> _sources;
    private readonly Dictionary<string, IReadOnlyList<CodexUnreadSource>> _byId;

    internal CodexUnreadSourceRegistry(IReadOnlyList<string> homes)
    {
        var sources = new List<CodexUnreadSource>();
        foreach (var home in homes)
        {
            var canonicalHome = CodexSourceIdentity.CanonicalizeHome(home);
            var sourceId = CodexSourceIdentity.ForHome(home);
            if (canonicalHome is null || sourceId is null) continue;

            var statePath = CodexUnreadStateReader.ResolveStatePath(canonicalHome);
            sources.Add(new(sourceId, canonicalHome, statePath, HostPartition,
                statePath is null ? CodexUnreadSourceHealth.Unavailable : CodexUnreadSourceHealth.Configured,
                statePath is null ? "invalid home path" : "exact canonical unread state path"));
        }

        _sources = sources;
        foreach (var duplicate in sources.GroupBy(source => source.SourceInstanceId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).SelectMany(group => group).ToArray())
        {
            var index = sources.IndexOf(duplicate);
            sources[index] = duplicate with
            {
                Health = CodexUnreadSourceHealth.Duplicate,
                Detail = "duplicate sourceInstanceId"
            };
        }

        _byId = sources.GroupBy(source => source.SourceInstanceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CodexUnreadSource>)group.ToArray(), StringComparer.Ordinal);
    }

    internal static CodexUnreadSourceRegistry Detect() => new(CodexHomeDiscovery.DetectHomes());

    public int SourceCount => _sources.Count;
    public IReadOnlyList<CodexUnreadSource> Sources => _sources;

    public CodexUnreadSnapshot Read(string sourceInstanceId, DateTimeOffset startedUtc)
    {
        // Legacy no-marker events route only when exactly one source exists.
        // Multiple homes make source-less unread evidence ambiguous.
        IReadOnlyList<CodexUnreadSource>? matches = null;
        if (string.IsNullOrWhiteSpace(sourceInstanceId))
        {
            if (_sources.Count == 1) matches = _sources;
        }
        else if (_byId.TryGetValue(sourceInstanceId, out var found))
        {
            matches = found;
        }

        if (matches is null || matches.Count != 1 || matches[0].Health == CodexUnreadSourceHealth.Duplicate)
            return CodexUnreadSnapshot.Failed(sourceInstanceId, HostPartition, startedUtc, CodexUnreadState.Unknown);

        var source = matches[0];
        var reader = new CodexUnreadStateReader(source.StatePath, source.HostPartition, source.SourceInstanceId);
        return reader.Read(startedUtc);
    }
}

internal sealed class SingleCodexUnreadSourceRegistry : ICodexUnreadSourceRegistry
{
    private readonly ICodexUnreadStateReader _reader;
    private readonly IReadOnlyList<CodexUnreadSource> _sources;

    internal SingleCodexUnreadSourceRegistry(ICodexUnreadStateReader reader)
    {
        _reader = reader;
        _sources = Array.Empty<CodexUnreadSource>();
    }

    public int SourceCount => 1;
    public IReadOnlyList<CodexUnreadSource> Sources => _sources;

    public CodexUnreadSnapshot Read(string sourceInstanceId, DateTimeOffset startedUtc)
    {
        if (!string.IsNullOrWhiteSpace(sourceInstanceId))
            return _reader.Read(startedUtc) with { SourceInstanceId = sourceInstanceId };
        return _reader.Read(startedUtc);
    }
}
