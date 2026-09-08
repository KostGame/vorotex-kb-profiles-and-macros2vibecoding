using Vorotex.K15.Clients;

namespace Vorotex.K15.Runtime.Tests;

internal static class PackageTests
{
    public static Task ResolverAcceptsActivePayloadAndRejectsStalePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "k15-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            var active = Path.Combine(root, "versions", "A", "payload");
            var stale = Path.Combine(root, "versions", "OLD", "payload");
            Directory.CreateDirectory(active); Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(root, VNextPackageLayout.CurrentVersionFileName), "A\n");
            var target = "Vorotex.K15.ControlCenter.exe";
            File.WriteAllText(Path.Combine(active, target), "active"); File.WriteAllText(Path.Combine(stale, target), "stale");
            TestAssert.Equal(Path.GetFullPath(Path.Combine(active, target)), VNextPackageLayout.ResolveExecutable(active, target), "active package was not resolved");
            TestAssert.True(VNextPackageLayout.ResolveExecutable(stale, target) is null, "stale package resolved an executable");
            TestAssert.True(VNextPackageLayout.ResolveExecutable(active, "missing.exe") is null, "missing executable resolved");
            return Task.CompletedTask;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    public static Task ResolverSupportsOnlyBoundedSplitSiblingLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "k15-split-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tray = Path.Combine(root, "status-tray"); var dashboard = Path.Combine(root, "live-dashboard");
            Directory.CreateDirectory(tray); Directory.CreateDirectory(dashboard);
            var target = Path.Combine(dashboard, "Vorotex.K15.LiveDashboard.exe"); File.WriteAllText(target, "dashboard");
            TestAssert.Equal(Path.GetFullPath(target), VNextPackageLayout.ResolveExecutable(tray, Path.GetFileName(target)), "split sibling was not resolved");
            TestAssert.True(VNextPackageLayout.ResolveExecutable(Path.Combine(root, "random"), Path.GetFileName(target)) is null, "broad layout search resolved");
            return Task.CompletedTask;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    public static Task ResolverFailsClosedForMalformedSelectorAndUnknownExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "k15-malformed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var payload = Path.Combine(root, "versions", "A", "payload"); Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(root, VNextPackageLayout.CurrentVersionFileName), "../escape\n");
            File.WriteAllText(Path.Combine(payload, "Vorotex.K15.Runtime.exe"), "runtime");
            TestAssert.True(VNextPackageLayout.ResolveExecutable(payload, "Vorotex.K15.Runtime.exe") is null, "malformed selector resolved");
            TestAssert.True(VNextPackageLayout.ResolveExecutable(payload, "not-vnext.exe") is null, "unknown executable resolved");
            File.WriteAllText(Path.Combine(root, VNextPackageLayout.CurrentVersionFileName), "A\n");
            File.Delete(Path.Combine(payload, "Vorotex.K15.Runtime.exe"));
            TestAssert.True(VNextPackageLayout.ResolveExecutable(payload, "Vorotex.K15.Runtime.exe") is null, "disappeared executable resolved");
            return Task.CompletedTask;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
