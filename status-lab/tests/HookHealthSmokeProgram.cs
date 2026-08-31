using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "vorotex-hook-health-" + Guid.NewGuid().ToString("N"));
var localAppData = Path.Combine(root, "localappdata");
var stable = Path.Combine(localAppData, "VorotexK15", "app", "hooks", "codex-hook-logger.ps1");
var oldLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

try
{
    Directory.CreateDirectory(Path.GetDirectoryName(stable)!);
    Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);

    var missingHome = MakeHome(root, "missing", stable);
    var missing = CodexHookHealth.InspectHomes(new[] { missingHome });
    Require(!missing.Healthy && missing.Detail.Contains("target missing", StringComparison.Ordinal),
        "Health must reject a missing logger target.");

    File.WriteAllText(stable, "# synthetic logger");
    var transientTarget = Path.Combine(root, "build (1)", "codex-hook-logger.ps1");
    Directory.CreateDirectory(Path.GetDirectoryName(transientTarget)!);
    File.WriteAllText(transientTarget, "# transient");
    var transientHome = MakeHome(root, "transient", transientTarget);
    var transient = CodexHookHealth.InspectHomes(new[] { transientHome });
    Require(!transient.Healthy && transient.Detail.Contains("transient numbered build path", StringComparison.Ordinal),
        "Health must report a transient numbered logger path.");

    var driftTarget = Path.Combine(root, "other", "codex-hook-logger.ps1");
    Directory.CreateDirectory(Path.GetDirectoryName(driftTarget)!);
    File.WriteAllText(driftTarget, "# drift");
    var healthyHome = MakeHome(root, "healthy", stable);
    var driftedHome = MakeHome(root, "drifted", driftTarget);
    var mixed = CodexHookHealth.InspectHomes(new[] { healthyHome, driftedHome });
    Require(mixed.HomesFound == 2 && mixed.HealthyHomes == 1 && !mixed.Healthy,
        "Mixed healthy/drifted homes must not report актуальны.");
    Require(mixed.Detail.Contains(driftedHome, StringComparison.Ordinal),
        "Health detail must identify the exact affected Codex home.");

    Console.WriteLine("CodexHookHealth missing-target, transient-path, path-drift and mixed-home tests: PASS");
}
finally
{
    Environment.SetEnvironmentVariable("LOCALAPPDATA", oldLocalAppData);
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static string MakeHome(string root, string name, string loggerPath)
{
    var home = Path.Combine(root, name);
    Directory.CreateDirectory(home);
    var events = new[] { "UserPromptSubmit", "PermissionRequest", "PreToolUse", "PostToolUse", "Stop", "SessionEnd" };
    var entries = string.Join(",", events.Select(eventName =>
        $"\"{eventName}\":[{{\"hooks\":[{{\"type\":\"command\",\"commandWindows\":\"powershell.exe -File \\\"{loggerPath.Replace("\\", "\\\\")}\\\"\"}}]}}]"));
    File.WriteAllText(Path.Combine(home, "hooks.json"), "{\"hooks\":{" + entries + "}}");
    return home;
}
