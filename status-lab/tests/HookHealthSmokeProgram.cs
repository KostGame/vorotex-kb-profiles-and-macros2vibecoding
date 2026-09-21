using System.Text.Json;
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

    var correctHome = MakeHome(root, "correct", stable);
    var correct = CodexHookHealth.InspectHomes(new[] { correctHome });
    Require(correct.Healthy && correct.Detail.Contains("OK", StringComparison.Ordinal),
        "Canonical sourceInstanceId must report healthy.");

    var missingIdHome = MakeHome(root, "missing-id", stable, sourceId: "");
    var missingId = CodexHookHealth.InspectHomes(new[] { missingIdHome });
    Require(!missingId.Healthy && missingId.Detail.Contains("missing or malformed sourceInstanceId", StringComparison.Ordinal),
        "Missing sourceInstanceId must be unhealthy.");

    var wrongIdHome = MakeHome(root, "wrong-id", stable, sourceId: CodexSourceIdentity.ForHome(correctHome));
    var wrongId = CodexHookHealth.InspectHomes(new[] { wrongIdHome });
    Require(!wrongId.Healthy && wrongId.Detail.Contains("wrong sourceInstanceId", StringComparison.Ordinal),
        "SourceInstanceId from another home must be unhealthy.");

    var duplicateA = MakeHome(root, "duplicate-a", stable, sourceId: "local:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    var duplicateB = MakeHome(root, "duplicate-b", stable, sourceId: "local:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    var duplicate = CodexHookHealth.InspectHomes(new[] { duplicateA, duplicateB });
    Require(!duplicate.Healthy && duplicate.Detail.Contains("duplicate sourceInstanceId", StringComparison.Ordinal),
        "Duplicate sourceInstanceId must be unhealthy.");

    var staleHome = MakeHome(root, "stale", stable);
    var staleHooksPath = Path.Combine(staleHome, "hooks.json");
    using (var staleDocument = JsonDocument.Parse(File.ReadAllText(staleHooksPath)))
    {
        var rootNode = JsonSerializer.Deserialize<Dictionary<string, object>>(staleDocument.RootElement.GetRawText())!;
        var hooksNode = JsonSerializer.Deserialize<Dictionary<string, object>>(staleDocument.RootElement.GetProperty("hooks").GetRawText())!;
        hooksNode["SessionStart"] = JsonSerializer.Deserialize<object>("[{\"hooks\":[{\"type\":\"command\",\"commandWindows\":\"powershell.exe -File \\\"" + stable.Replace("\\", "\\\\") + "\\\" -SourceInstanceId \\\"" + CodexSourceIdentity.ForHome(staleHome) + "\\\"\"}]}]")!;
        rootNode["hooks"] = hooksNode;
        File.WriteAllText(staleHooksPath, JsonSerializer.Serialize(rootNode));
    }
    var staleResult = CodexHookHealth.InspectHomes(new[] { staleHome });
    Require(!staleResult.Healthy && staleResult.Detail.Contains("stale/unexpected Status Lab event SessionStart", StringComparison.Ordinal),
        "Stale Status Lab event must be unhealthy.");

    Console.WriteLine("CodexHookHealth source identity, missing-target, transient-path, path-drift and mixed-home tests: PASS");
}
finally
{
    Environment.SetEnvironmentVariable("LOCALAPPDATA", oldLocalAppData);
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static string MakeHome(string root, string name, string loggerPath, string? sourceId = null)
{
    var home = Path.Combine(root, name);
    Directory.CreateDirectory(home);
    sourceId ??= CodexSourceIdentity.ForHome(home)!;
    var events = new[] { "UserPromptSubmit", "PermissionRequest", "PreToolUse", "PostToolUse", "Stop", "SessionEnd" };
    var entries = string.Join(",", events.Select(eventName =>
        $"\"{eventName}\":[{{\"hooks\":[{{\"type\":\"command\",\"commandWindows\":\"powershell.exe -File \\\"{loggerPath.Replace("\\", "\\\\")}\\\" -SourceInstanceId \\\"{sourceId}\\\"\"}}]}}]"));
    File.WriteAllText(Path.Combine(home, "hooks.json"), "{\"hooks\":{" + entries + "}}");
    return home;
}
