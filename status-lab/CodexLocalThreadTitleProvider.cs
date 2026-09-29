using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal interface ICodexLocalThreadTitleProvider
{
    Task<CodexLocalThreadMetadata> ReadTitlesAsync(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        IReadOnlyList<CodexUnreadSource> trustedSources,
        CancellationToken cancellationToken);
}

internal sealed record CodexLocalThreadMetadata(
    IReadOnlyDictionary<string, string> NamesBySourceAndThread,
    IReadOnlySet<string> PersistedThreadKeys)
{
    internal static CodexLocalThreadMetadata Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

internal sealed record CodexLocalThreadTitleSource(
    string SourceInstanceId,
    string CodexHomePath,
    IReadOnlyList<string> ThreadIds);

internal static class CodexLocalThreadTitleSourceResolver
{
    internal static IReadOnlyList<CodexLocalThreadTitleSource> Resolve(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        IReadOnlyList<CodexUnreadSource> trustedSources)
    {
        var registryById = trustedSources
            .Where(source => CodexSourceIdentity.IsValid(source.SourceInstanceId))
            .GroupBy(source => source.SourceInstanceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var rowsById = sessions
            .Where(session => (session.IsAlive || session.State == K15NormalizedState.DonePendingAttention) &&
                CodexSourceIdentity.IsValid(session.SourceInstanceId) && IsBoundedThreadId(session.ThreadId))
            .GroupBy(session => session.SourceInstanceId, StringComparer.Ordinal);
        var requests = new List<CodexLocalThreadTitleSource>();

        foreach (var rows in rowsById)
        {
            if (!registryById.TryGetValue(rows.Key, out var matches) || matches.Length != 1)
                continue;
            var source = matches[0];
            var home = CodexSourceIdentity.CanonicalizeHome(source.CodexHomePath);
            if (source.Health == CodexUnreadSourceHealth.Duplicate || home is null ||
                !string.Equals(home, source.CodexHomePath, StringComparison.Ordinal) ||
                !string.Equals(CodexSourceIdentity.ForHome(home), source.SourceInstanceId, StringComparison.Ordinal) ||
                !Directory.Exists(home))
                continue;

            var threadIds = rows.Select(session => session.ThreadId!)
                .Distinct(StringComparer.Ordinal).Take(20).ToArray();
            if (threadIds.Length > 0)
                requests.Add(new(source.SourceInstanceId, home, threadIds));
        }

        return requests;
    }

    private static bool IsBoundedThreadId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}

// Reads only stock app-server metadata. Lifecycle and unread evidence never enter this provider.
internal sealed class CodexAppServerLocalThreadTitleProvider : ICodexLocalThreadTitleProvider
{
    private const int MaxThreadIds = 20;
    private const int MaxLineBytes = 128 * 1024;
    private const int MaxMessagesPerResponse = 64;

    public async Task<CodexLocalThreadMetadata> ReadTitlesAsync(
        IReadOnlyList<CodexSessionSnapshot> sessions,
        IReadOnlyList<CodexUnreadSource> trustedSources,
        CancellationToken cancellationToken)
    {
        var requests = CodexLocalThreadTitleSourceResolver.Resolve(sessions, trustedSources);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var persistedThreadKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = CodexLocalSessionIndexReader.Read(request.CodexHomePath,
                request.ThreadIds.ToHashSet(StringComparer.Ordinal));
            foreach (var threadId in index.PersistedThreadIds)
                persistedThreadKeys.Add(CodexSourceIdentity.CompositeKey(request.SourceInstanceId, threadId));
            foreach (var (threadId, name) in index.NamesByThreadId)
            {
                var key = CodexSourceIdentity.CompositeKey(request.SourceInstanceId, threadId);
                if (!titles.ContainsKey(key)) titles[key] = name;
            }

            try
            {
                var sourceTitles = await ReadSourceTitlesAsync(request, cancellationToken).ConfigureAwait(false);
                foreach (var (threadId, name) in sourceTitles)
                    titles[CodexSourceIdentity.CompositeKey(request.SourceInstanceId, threadId)] = name;
            }
            catch (OperationCanceledException)
            {
                // The persisted exact-home result remains useful when the
                // optional app-server enrichment times out or is cancelled.
                break;
            }
        }
        return new(titles, persistedThreadKeys);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadSourceTitlesAsync(
        CodexLocalThreadTitleSource source, CancellationToken cancellationToken)
    {
        var ids = source.ThreadIds.Where(IsBoundedId).Distinct(StringComparer.Ordinal)
            .Take(MaxThreadIds).ToArray();
        if (ids.Length == 0) return new Dictionary<string, string>(StringComparer.Ordinal);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "codex",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.Environment["CODEX_HOME"] = source.CodexHomePath;

        try
        {
            if (!process.Start()) return new Dictionary<string, string>(StringComparer.Ordinal);
            _ = DrainStandardErrorAsync(process, cancellationToken);
            long requestId = 1;
            await SendRequestAsync(process, requestId, "initialize", new
            {
                clientInfo = new { name = "VorotexStatusLab", version = "1.0.0" },
                capabilities = new { experimentalApi = false }
            }, cancellationToken);
            if (await ReadMatchingResponseAsync(process, requestId,
                    root => root.TryGetProperty("result", out _) ? "initialized" : null,
                    cancellationToken) is null)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}");
            await process.StandardInput.FlushAsync(cancellationToken);

            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var threadId in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                requestId++;
                await SendRequestAsync(process, requestId, "thread/read",
                    new { threadId, includeTurns = false }, cancellationToken);
                var name = await ReadMatchingResponseAsync(process, requestId,
                    root => CodexThreadReadTitleParser.Parse(root, threadId), cancellationToken);
                if (name is not null) titles[threadId] = name;
            }
            return titles;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(cleanup.Token);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static async Task SendRequestAsync(Process process, long id, string method, object parameters,
        CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<string?> ReadMatchingResponseAsync(Process process, long id,
        Func<JsonElement, string?> safeSelector, CancellationToken cancellationToken)
    {
        for (var index = 0; index < MaxMessagesPerResponse; index++)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null || Encoding.UTF8.GetByteCount(line) > MaxLineBytes) return null;
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var responseId))
                    continue;
                if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt64(out var actualId) || actualId != id)
                    return null;
                if (root.TryGetProperty("error", out _)) return null;
                return safeSelector(root);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    private static async Task DrainStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is not null) { }
        }
        catch { }
    }

    private static bool IsBoundedId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}

internal sealed record CodexLocalSessionIndex(
    IReadOnlySet<string> PersistedThreadIds,
    IReadOnlyDictionary<string, string> NamesByThreadId);

// Reads only exact identity and optional user-visible name from the registered
// home's persisted session index. Other session metadata is ignored.
internal static class CodexLocalSessionIndexReader
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 8192;
    private const int MaxLineBytes = 64 * 1024;

    internal static CodexLocalSessionIndex Read(string exactCodexHome, IReadOnlySet<string> requestedThreadIds)
    {
        var empty = new CodexLocalSessionIndex(new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
        if (CodexSourceIdentity.CanonicalizeHome(exactCodexHome) is not { } canonicalHome ||
            !string.Equals(canonicalHome, exactCodexHome, StringComparison.Ordinal) ||
            requestedThreadIds.Count == 0)
            return empty;

        var path = Path.Combine(canonicalHome, "session_index.jsonl");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > MaxBytes) return empty;
            using var memory = new MemoryStream((int)stream.Length);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (memory.Length + read > MaxBytes) return empty;
                memory.Write(buffer, 0, read);
            }

            var persisted = new HashSet<string>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            using var reader = new StringReader(Encoding.UTF8.GetString(memory.ToArray()));
            var entries = 0;
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (++entries > MaxEntries || Encoding.UTF8.GetByteCount(line) > MaxLineBytes ||
                    !TryReadEntry(line, out var id, out var name)) return empty;
                if (!seen.Add(id)) duplicates.Add(id);
                if (!requestedThreadIds.Contains(id)) continue;
                persisted.Add(id);
                if (name is not null) names[id] = name;
            }

            foreach (var id in duplicates)
            {
                names.Remove(id);
            }
            return new(persisted, names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   ArgumentException or NotSupportedException)
        {
            return empty;
        }
    }

    private static bool TryReadEntry(string line, out string id, out string? name)
    {
        id = string.Empty;
        name = null;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                return false;
            id = idElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) return false;
            if (root.TryGetProperty("thread_name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                var candidate = nameElement.GetString()?.Trim();
                if (!string.IsNullOrEmpty(candidate) && Encoding.UTF8.GetByteCount(candidate) <= 256 &&
                    !candidate.Any(char.IsControl)) name = candidate;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal static class CodexThreadReadTitleParser
{
    private const int MaxResponseBytes = 128 * 1024;

    internal static string? Parse(string jsonRpcResponse, string exactThreadId)
    {
        if (string.IsNullOrWhiteSpace(jsonRpcResponse) ||
            Encoding.UTF8.GetByteCount(jsonRpcResponse) > MaxResponseBytes ||
            string.IsNullOrWhiteSpace(exactThreadId) || exactThreadId.Length > 256 ||
            exactThreadId.Any(char.IsControl))
            return null;

        try
        {
            using var document = JsonDocument.Parse(jsonRpcResponse, new JsonDocumentOptions { MaxDepth = 32 });
            return Parse(document.RootElement, exactThreadId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? Parse(JsonElement root, string exactThreadId)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object ||
            !TryString(thread, "id", out var id) || !string.Equals(id, exactThreadId, StringComparison.Ordinal) ||
            !TryString(thread, "name", out var name))
            return null;

        var safeName = name!.Trim();
        return safeName.Length > 0 && Encoding.UTF8.GetByteCount(safeName) <= 256 &&
               !safeName.Any(char.IsControl)
            ? safeName
            : null;
    }

    private static bool TryString(JsonElement element, string propertyName, out string? value)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }
        value = null;
        return false;
    }
}
