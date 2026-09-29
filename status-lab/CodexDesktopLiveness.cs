using System.ComponentModel;
using System.Diagnostics;

namespace Vorotex.K15.StatusLab;

internal sealed record CodexProcessCandidate(
    string ProcessName,
    string? ExecutablePath,
    bool InspectionFailed = false);

internal interface ICodexProcessSnapshotSource
{
    IReadOnlyList<CodexProcessCandidate> ReadCandidates();
}

internal sealed class WindowsCodexProcessSnapshotSource : ICodexProcessSnapshotSource
{
    private static readonly string[] CandidateProcessNames = ["ChatGPT", "Codex"];

    public IReadOnlyList<CodexProcessCandidate> ReadCandidates()
    {
        var result = new List<CodexProcessCandidate>();
        try
        {
            foreach (var processName in CandidateProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try
                        {
                            result.Add(new(process.ProcessName, process.MainModule?.FileName));
                        }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                            result.Add(new(process.ProcessName, null, InspectionFailed: true));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return [new("probe", null, InspectionFailed: true)];
        }

        return result;
    }
}

internal sealed class WindowsCodexLivenessProvider : ICodexLivenessProvider
{
    private readonly ICodexProcessSnapshotSource _processes;

    internal WindowsCodexLivenessProvider()
        : this(new WindowsCodexProcessSnapshotSource())
    {
    }

    internal WindowsCodexLivenessProvider(ICodexProcessSnapshotSource processes)
    {
        _processes = processes;
    }

    public CodexLivenessState GetLiveness() => Classify(_processes.ReadCandidates());

    internal static CodexLivenessState Classify(IEnumerable<CodexProcessCandidate> candidates)
    {
        var inspectionFailed = false;
        foreach (var candidate in candidates)
        {
            if (!IsRelevantProcessName(candidate.ProcessName))
                continue;

            if (candidate.InspectionFailed || string.IsNullOrWhiteSpace(candidate.ExecutablePath))
            {
                inspectionFailed = true;
                continue;
            }

            if (IsPrimaryCodexDesktopExecutable(candidate.ExecutablePath))
                return CodexLivenessState.Alive;
        }

        return inspectionFailed ? CodexLivenessState.Unknown : CodexLivenessState.NotRunning;
    }

    internal static bool IsPrimaryCodexDesktopExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('/', '\\');
        var fileName = Path.GetFileName(normalized);
        if (!fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var packageMarker = "\\WindowsApps\\OpenAI.Codex_";
        var packageIndex = normalized.IndexOf(packageMarker, StringComparison.OrdinalIgnoreCase);
        if (packageIndex < 0)
            return false;

        var appMarker = "\\app\\";
        var appIndex = normalized.IndexOf(appMarker, packageIndex + packageMarker.Length,
            StringComparison.OrdinalIgnoreCase);
        if (appIndex < 0)
            return false;

        var relative = normalized[(appIndex + appMarker.Length)..];
        return relative.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
               relative.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelevantProcessName(string processName) =>
        processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("Codex", StringComparison.OrdinalIgnoreCase);
}
