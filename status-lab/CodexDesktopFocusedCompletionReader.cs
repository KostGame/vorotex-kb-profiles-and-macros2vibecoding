using System.Diagnostics;
using System.Globalization;

namespace Vorotex.K15.StatusLab;

internal sealed record CodexFocusedCompletionProof(
    CodexCompletionKey Completion,
    DateTimeOffset DesktopCompletedUtc,
    int DesktopProcessId);

internal interface ICodexFocusedCompletionReader
{
    CodexFocusedCompletionProof? Read(CodexCompletionKey completion, DateTimeOffset nowUtc);
}

internal sealed class CodexDesktopFocusedCompletionReader : ICodexFocusedCompletionReader
{
    internal const long MaxLogBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan CompletionSkew = TimeSpan.FromSeconds(5);
    private readonly string _logsRoot;
    private readonly Func<int, CodexDesktopProcessIdentity?> _processLookup;

    internal sealed record CodexDesktopProcessIdentity(int ProcessId, string ExecutablePath, DateTimeOffset StartedUtc);

    internal CodexDesktopFocusedCompletionReader(
        string logsRoot,
        Func<int, CodexDesktopProcessIdentity?>? processLookup = null)
    {
        _logsRoot = logsRoot;
        _processLookup = processLookup ?? LookupProcess;
    }

    internal static CodexDesktopFocusedCompletionReader? CreateDefault()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return null;
        var root = Path.Combine(local, "Packages", "OpenAI.Codex_2p2nqsd0c76g0",
            "LocalCache", "Local", "Codex", "Logs");
        return Directory.Exists(root) ? new(root) : null;
    }

    public CodexFocusedCompletionProof? Read(CodexCompletionKey completion, DateTimeOffset nowUtc)
    {
        if (!CodexSourceIdentity.IsValid(completion.SourceInstanceId) ||
            !CodexUnreadStateReader.Bounded(completion.ThreadId) ||
            !CodexUnreadStateReader.Bounded(completion.TurnId))
            return null;

        foreach (var path in CandidateLogs(completion.CompletedUtc, nowUtc))
        {
            CodexFocusedCompletionProof? proof;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxLogBytes)
                    continue;
                if (!TryParseProcessId(info.Name, out var processId))
                    continue;
                var process = _processLookup(processId);
                if (process is null || !IsCodexDesktopExecutable(process.ExecutablePath))
                    continue;
                if (info.CreationTimeUtc < process.StartedUtc.UtcDateTime.AddSeconds(-10) ||
                    info.CreationTimeUtc > process.StartedUtc.UtcDateTime.AddMinutes(2) ||
                    info.LastWriteTimeUtc < completion.CompletedUtc.UtcDateTime.AddSeconds(-2))
                    continue;

                var text = File.ReadAllText(path);
                proof = ParseLog(text, completion, processId, process.StartedUtc);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (proof is not null)
                return proof;
        }
        return null;
    }

    private IEnumerable<string> CandidateLogs(DateTimeOffset completedUtc, DateTimeOffset nowUtc)
    {
        var completedDay = completedUtc.UtcDateTime.Date;
        var nowDay = nowUtc.UtcDateTime.Date;
        var days = new[]
        {
            completedDay.AddDays(-1), completedDay, completedDay.AddDays(1),
            nowDay.AddDays(-1), nowDay, nowDay.AddDays(1)
        }.Distinct().ToArray();

        foreach (var day in days)
        {
            var dir = Path.Combine(_logsRoot,
                day.Year.ToString("0000", CultureInfo.InvariantCulture),
                day.Month.ToString("00", CultureInfo.InvariantCulture),
                day.Day.ToString("00", CultureInfo.InvariantCulture));
            if (!Directory.Exists(dir)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "codex-desktop-*-t0-i*-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(12)
                    .ToArray();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
                yield return file;
        }
    }

    internal static CodexFocusedCompletionProof? ParseLog(
        string text,
        CodexCompletionKey completion,
        int processId,
        DateTimeOffset processStartedUtc)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string? activeConversation = null;
        var activeKnown = false;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!TryTimestamp(line, out var timestamp) || timestamp < processStartedUtc.AddSeconds(-10))
                continue;

            if (line.Contains("websocket_reconnect_recovery_", StringComparison.Ordinal) &&
                TryField(line, "currentConversationId=", out var current))
            {
                activeKnown = true;
                activeConversation = current == "null" || !CodexUnreadStateReader.Bounded(current)
                    ? null
                    : current;
            }

            if (line.Contains("IAB_LIFECYCLE received browser sidebar owner sync", StringComparison.Ordinal) &&
                TryField(line, "ownerRoutePath=", out var route))
            {
                activeKnown = true;
                activeConversation = ParseLocalRoute(route);
            }

            if (line.Contains("Reasoning summary turn-start config resolved", StringComparison.Ordinal) &&
                TryField(line, "conversationId=", out var turnStartConversationId) &&
                CodexUnreadStateReader.Bounded(turnStartConversationId) &&
                line.Contains("rendererWindowAppearance=primary", StringComparison.Ordinal) &&
                line.Contains("rendererWindowFocused=true", StringComparison.Ordinal) &&
                line.Contains("rendererWindowVisible=true", StringComparison.Ordinal))
            {
                activeKnown = true;
                activeConversation = turnStartConversationId;
            }

            if (!line.Contains("[desktop-notifications] received turn-complete", StringComparison.Ordinal) ||
                !TryField(line, "conversationId=", out var conversationId) ||
                !TryField(line, "turnId=", out var turnId) ||
                conversationId != completion.ThreadId ||
                turnId != completion.TurnId)
                continue;

            if (timestamp < completion.CompletedUtc ||
                timestamp - completion.CompletedUtc > CompletionSkew ||
                !activeKnown ||
                activeConversation != completion.ThreadId ||
                !line.Contains("rendererWindowFocused=true", StringComparison.Ordinal) ||
                !line.Contains("rendererWindowVisible=true", StringComparison.Ordinal) ||
                !line.Contains("rendererWindowAppearance=primary", StringComparison.Ordinal))
                return null;

            return new(completion, timestamp, processId);
        }
        return null;
    }

    internal static string? ParseLocalRoute(string route)
    {
        if (!route.StartsWith("/local/", StringComparison.Ordinal))
            return null;
        var value = route["/local/".Length..];
        var cut = value.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) value = value[..cut];
        try { value = Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return null; }
        return CodexUnreadStateReader.Bounded(value) ? value : null;
    }

    internal static bool TryParseProcessId(string fileName, out int processId)
    {
        processId = 0;
        var marker = "-t0-";
        var markerIndex = fileName.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0) return false;
        var prefix = fileName[..markerIndex];
        var dash = prefix.LastIndexOf('-');
        return dash >= 0 && int.TryParse(prefix[(dash + 1)..], NumberStyles.None,
            CultureInfo.InvariantCulture, out processId) && processId > 0;
    }

    private static bool TryTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var space = line.IndexOf(' ');
        if (space <= 0) return false;
        return DateTimeOffset.TryParse(line[..space], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    private static bool TryField(string line, string prefix, out string value)
    {
        value = string.Empty;
        var start = line.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return false;
        start += prefix.Length;
        var end = line.IndexOf(' ', start);
        if (end < 0) end = line.Length;
        if (end <= start) return false;
        value = line[start..end].Trim('"');
        return value.Length > 0 && value.Length <= 1024;
    }

    private static CodexDesktopProcessIdentity? LookupProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || !string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
                return null;
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return null;
            return new(processId, path, process.StartTime.ToUniversalTime());
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    internal static bool IsCodexDesktopExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('/', '\\');
        return normalized.StartsWith(@"C:\Program Files\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
               normalized.EndsWith(@"\app\ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
    }
}
