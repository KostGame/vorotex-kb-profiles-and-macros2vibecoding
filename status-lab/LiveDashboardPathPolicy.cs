namespace Vorotex.K15.StatusLab;

internal static class LiveDashboardPathPolicy
{
    internal const string DefaultExecutableName = "Vorotex.K15.LiveDashboard.exe";

    internal static string? Resolve(string baseDirectory, string executableName = DefaultExecutableName)
    {
        var colocated = Path.Combine(baseDirectory, executableName);
        if (File.Exists(colocated)) return colocated;

        var parent = Directory.GetParent(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null) return null;

        var splitSibling = Path.Combine(parent.FullName, "live-dashboard", executableName);
        return File.Exists(splitSibling) ? splitSibling : null;
    }
}
