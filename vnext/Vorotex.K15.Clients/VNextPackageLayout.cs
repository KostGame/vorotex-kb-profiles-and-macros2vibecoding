namespace Vorotex.K15.Clients;

/// <summary>Bounded executable resolution for the vNext side-by-side package.</summary>
public static class VNextPackageLayout
{
    private static readonly HashSet<string> AllowedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vorotex.K15.Runtime.exe", "Vorotex.K15.StatusTray.exe", "Vorotex.K15.ControlCenter.exe", "Vorotex.K15.LiveDashboard.exe"
    };
    public const string CurrentVersionFileName = "current-version.txt";
    public const string PayloadDirectoryName = "payload";

    public static string? ResolveExecutable(string applicationBaseDirectory, string executableName)
    {
        try
        {
        if (string.IsNullOrWhiteSpace(applicationBaseDirectory) || string.IsNullOrWhiteSpace(executableName) ||
            !AllowedExecutables.Contains(executableName) || Path.GetFileName(executableName) != executableName)
            return null;

        var baseDirectory = Path.GetFullPath(applicationBaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var packageRoot = FindPackageRoot(baseDirectory);
        if (packageRoot is not null)
        {
            var version = ReadCurrentVersion(packageRoot);
            if (version is null) return null;
            var activePayload = Path.Combine(packageRoot, "versions", version, PayloadDirectoryName);
            if (!IsWithin(baseDirectory, activePayload)) return null;
            return ExistingCandidate(Path.Combine(activePayload, executableName), executableName, activePayload);
        }

        // Compatibility for the accepted co-located/split candidate package only.
        var colocated = Path.Combine(baseDirectory, executableName);
        if (File.Exists(colocated)) return colocated;
        var sourceRole = new DirectoryInfo(baseDirectory).Name.ToLowerInvariant();
        if (sourceRole is not ("status-tray" or "control-center" or "live-dashboard" or "runtime")) return null;
        var role = executableName.ToLowerInvariant() switch
        {
            "vorotex.k15.statustray.exe" => "status-tray",
            "vorotex.k15.controlcenter.exe" => "control-center",
            "vorotex.k15.livedashboard.exe" => "live-dashboard",
            "vorotex.k15.runtime.exe" => "runtime",
            _ => null
        };
        return role is null ? null : ExistingCandidate(Path.Combine(baseDirectory, "..", role, executableName), executableName, Directory.GetParent(baseDirectory)!.FullName);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static string? ExistingCandidate(string candidate, string executableName, string allowedRoot)
    {
        var full = Path.GetFullPath(candidate);
        return IsWithin(full, allowedRoot) && File.Exists(full) && Path.GetFileName(full) == executableName ? full : null;
    }

    private static string? FindPackageRoot(string directory)
    {
        var current = new DirectoryInfo(directory);
        for (var i = 0; i < 5 && current is not null; i++, current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, CurrentVersionFileName))) return current.FullName;
        return null;
    }

    private static string? ReadCurrentVersion(string root)
    {
        var value = File.ReadAllText(Path.Combine(root, CurrentVersionFileName)).Trim();
        return value.Length is > 0 and <= 128 && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value is not "." and not ".." ? value : null;
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
