using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vorotex.K15.StatusLab;

if (args.Length == 3 && args[0] == "--assembly")
{
    var assembly = Assembly.LoadFrom(Path.GetFullPath(args[1]));
    var provenance = BuildProvenance.FromAssembly(assembly);
    var expected = args[2].ToLowerInvariant() switch
    {
        "stable" => BuildChannel.Stable,
        "canary" => BuildChannel.Canary,
        _ => BuildChannel.Unknown
    };
    Assert(provenance.Channel == expected, $"App assembly should resolve as {expected}.");
    Assert(expected == BuildChannel.Unknown ? !provenance.IsKnown : provenance.IsKnown,
        "Known/unknown assembly provenance state should match the expected channel.");
    if (expected != BuildChannel.Unknown)
        Assert(provenance.Commit == new string('a', 40), "Stamped assembly commit should survive MSBuild emission.");
    Console.WriteLine($"Embedded {provenance.ChannelLabel} build metadata: PASS");
    return;
}

if (args.Length == 1 && args[0] == "--bridge-default")
{
    var provenance = BridgeProvenanceReader.ReadDefault();
    Console.WriteLine(provenance.FormatLine());
    Assert(provenance.Health != "UNAVAILABLE", "Canonical live bridge provenance should be readable on this host.");
    return;
}

var stable = Metadata("stable");
Assert(stable.Channel == BuildChannel.Stable && stable.IsKnown, "Valid stable metadata should be STABLE.");
Assert(stable.FormatSummary().Contains("Commit: " + new string('a', 40), StringComparison.Ordinal), "Summary should include full commit.");
Assert(BuildProvenance.Parse(new Dictionary<string, string?>()).Channel == BuildChannel.Unknown, "Missing metadata should fail closed.");
Assert(BuildProvenance.Parse(MetadataMap(channel: "stable", issue: "231")).Channel == BuildChannel.Unknown, "Stable must reject a canary issue.");
Assert(BuildProvenance.Parse(MetadataMap(channel: "stable", pullRequest: "42")).Channel == BuildChannel.Unknown, "Stable must reject a canary PR.");
Assert(BuildProvenance.Parse(MetadataMap(channel: "canary")).Channel == BuildChannel.Unknown, "Canary must identify an issue or PR.");
Assert(BuildProvenance.Parse(MetadataMap(channel: "canary", issue: "0")).Channel == BuildChannel.Unknown, "Canary issue ID must be positive and bounded.");
Assert(BuildProvenance.Parse(MetadataMap(channel: "canary", issue: "231")).Channel == BuildChannel.Canary, "Valid canary issue metadata should be CANARY.");
Assert(BuildProvenance.Parse(MetadataMap(commit: new string('A', 40))).Channel == BuildChannel.Unknown, "Commit must use 40 lowercase hex characters.");
Assert(BuildProvenance.Parse(MetadataMap(commit: "bad")).Channel == BuildChannel.Unknown, "Malformed commit should fail closed.");
Assert(BuildProvenance.Parse(MetadataMap(buildUtc: "yesterday")).Channel == BuildChannel.Unknown, "Malformed timestamp should fail closed.");
Assert(BuildProvenance.Parse(MetadataMap(sourceRef: new string('x', 121))).Channel == BuildChannel.Unknown, "Source ref must remain bounded.");
var unexpected = MetadataMap();
unexpected["Unexpected"] = "value";
Assert(BuildProvenance.Parse(unexpected).Channel == BuildChannel.Unknown, "Unexpected K15 build metadata keys must fail closed.");
Assert(stable.SourceDisplay == "agent/231-build-provenance-ui", "Stable source ref should be displayed explicitly.");
var canaryWithScope = Metadata("canary", issue: "231", pullRequest: "233");
Assert(canaryWithScope.SourceDisplay.Contains("Issue #231", StringComparison.Ordinal) &&
       canaryWithScope.SourceDisplay.Contains("PR #233", StringComparison.Ordinal),
    "Canary source display should include issue/PR scope.");
Assert(canaryWithScope.ChannelDisplay == "CANARY #231", "Canary channel display should expose its issue id.");
var tooltip = canaryWithScope.FormatTooltip("RUNNING", rgbEnabled: false);
Assert(tooltip.Length <= 63 && tooltip.Contains("CANARY", StringComparison.Ordinal) &&
       tooltip.Contains("RUNNING", StringComparison.Ordinal),
    "Tooltip must stay within NotifyIcon limits while exposing channel and state.");
Assert(BuildProvenance.Unknown.FormatTooltip("NORMAL", rgbEnabled: false).Count(
           c => c == 'U') >= 1 &&
       !BuildProvenance.Unknown.FormatTooltip("NORMAL", rgbEnabled: false).Contains("UNKNOWN BUILD · UNKNOWN BUILD", StringComparison.Ordinal),
    "Unknown tooltip must not duplicate the UNKNOWN BUILD marker.");
var duplicated = new[]
{
    new AssemblyMetadataAttribute("K15.Build.Version", "1.2.3"),
    new AssemblyMetadataAttribute("K15.Build.Commit", new string('a', 40)),
    new AssemblyMetadataAttribute("K15.Build.Channel", "stable"),
    new AssemblyMetadataAttribute("K15.Build.Channel", "canary"),
    new AssemblyMetadataAttribute("K15.Build.BuildUtc", "2026-10-03T00:00:00Z")
};
Assert(BuildProvenance.FromMetadataAttributes(duplicated).Channel == BuildChannel.Unknown, "Duplicate build metadata keys must fail closed.");
Assert(stable.ChannelLabel == "STABLE" && Metadata("canary", issue: "231").ChannelLabel == "CANARY", "Stable/canary labels should be distinct.");

TestBridgeReader();
Console.WriteLine("Build/bridge provenance contract: PASS");

static BuildProvenance Metadata(string channel, string? issue = null, string? pullRequest = null) =>
    BuildProvenance.Parse(MetadataMap(channel: channel, issue: issue, pullRequest: pullRequest));

static Dictionary<string, string?> MetadataMap(
    string channel = "stable",
    string? commit = null,
    string? issue = null,
    string? pullRequest = null,
    string? buildUtc = null,
    string? sourceRef = "agent/231-build-provenance-ui") => new(StringComparer.Ordinal)
    {
        ["Version"] = "1.2.3",
        ["Commit"] = commit ?? new string('a', 40),
        ["Channel"] = channel,
        ["CanaryIssue"] = issue,
        ["CanaryPullRequest"] = pullRequest,
        ["BuildUtc"] = buildUtc ?? "2026-10-03T12:34:56Z",
        ["SourceRef"] = sourceRef
    };

static void TestBridgeReader()
{
    var root = Path.Combine(Path.GetTempPath(), "k15-bridge-provenance-" + Guid.NewGuid().ToString("N"));
    var statePath = Path.Combine(root, "activation-state.json");
    var manifestDirectory = Path.Combine(root, "canary", "issue199-repin-be3f-20261002");
    var manifestPath = Path.Combine(manifestDirectory, "manifest.json");
    Directory.CreateDirectory(manifestDirectory);
    try
    {
        var generation = "generation-123";
        var manifest = BuildManifest(root, generation);
        File.WriteAllBytes(manifestPath, manifest);
        var hash = Convert.ToHexString(SHA256.HashData(manifest));
        WriteState(statePath, manifestPath, hash, generation);

        var consistent = BridgeProvenanceReader.Read(statePath, root);
        Assert(consistent.Generation == generation, "Bridge reader should derive runtime generation from manifest childPath.");
        Assert(consistent.Identity.EndsWith("issue199-repin-be3f-20261002", StringComparison.Ordinal), "Bridge identity should use the bounded immutable bundle leaf without implying an app channel.");
        Assert(consistent.Health.StartsWith("CONSISTENT", StringComparison.Ordinal), "Matching state hash and generation should report consistency.");
        Assert(consistent.Health.Contains("LIVE HEALTH NOT CHECKED", StringComparison.Ordinal), "Provenance consistency must not claim live runtime health.");

        WriteState(statePath, manifestPath, new string('0', 64), generation);
        Assert(BridgeProvenanceReader.Read(statePath, root).Health.Contains("HASH MISMATCH", StringComparison.Ordinal), "Manifest hash mismatch should request revalidation.");
        WriteState(statePath, manifestPath, hash, "other-generation");
        Assert(BridgeProvenanceReader.Read(statePath, root).Health.Contains("GENERATION MISMATCH", StringComparison.Ordinal), "Approved generation mismatch should request revalidation.");

        var outsideManifest = Path.Combine(root + "-elsewhere", "manifest.json");
        WriteState(statePath, outsideManifest, hash, generation);
        Assert(BridgeProvenanceReader.Read(statePath, root).Health == "UNAVAILABLE", "Manifest path outside canonical bridge root must be unavailable.");

        File.WriteAllText(statePath, "{ malformed");
        Assert(BridgeProvenanceReader.Read(statePath, root).Health == "UNAVAILABLE", "Malformed activation state must fail closed.");

        WriteState(statePath, manifestPath, hash, generation, includeUnexpected: true);
        Assert(BridgeProvenanceReader.Read(statePath, root).Health == "UNAVAILABLE", "Unexpected activation-state fields must fail closed.");

        WriteDuplicateState(statePath, manifestPath, hash, generation);
        Assert(BridgeProvenanceReader.Read(statePath, root).Health == "UNAVAILABLE", "Duplicate activation-state fields must fail closed.");

        var validManifestText = Encoding.UTF8.GetString(manifest);
        var unexpectedManifest = Encoding.UTF8.GetBytes(validManifestText[..^1] + ",\"unexpected\":\"x\"}");
        File.WriteAllBytes(manifestPath, unexpectedManifest);
        WriteState(statePath, manifestPath, Convert.ToHexString(SHA256.HashData(unexpectedManifest)), generation);
        Assert(BridgeProvenanceReader.Read(statePath, root).Health == "UNAVAILABLE", "Unexpected manifest fields must fail closed.");

        var duplicateManifest = Encoding.UTF8.GetBytes(
            validManifestText[..^1] + ",\"childPath\":" +
            JsonSerializer.Serialize(Path.Combine(root, "external-codex", generation, "codex.exe")) + "}");
        File.WriteAllBytes(manifestPath, duplicateManifest);
        WriteState(statePath, manifestPath, Convert.ToHexString(SHA256.HashData(duplicateManifest)), generation);
        Assert(BridgeProvenanceReader.Read(statePath, root).Health == "UNAVAILABLE", "Duplicate manifest fields must fail closed.");

        File.WriteAllBytes(manifestPath, manifest);
        File.WriteAllText(statePath, JsonSerializer.Serialize(new
        {
            schema = "k15-codex-bridge/activation-state-v2",
            manifestPath,
            original = new { }
        }));
        Assert(BridgeProvenanceReader.Read(statePath, root).Health.Contains("LEGACY", StringComparison.Ordinal), "Valid legacy v2 state must remain explicitly unverified.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static byte[] BuildManifest(string root, string generation) =>
    JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "k15-codex-bridge/production-manifest-v2",
        adapterPath = Path.Combine(root, "adapter.exe"),
        nodePath = Path.Combine(root, "node.exe"),
        wrapperPath = Path.Combine(root, "approval-wrapper.mjs"),
        transparentWrapperPath = Path.Combine(root, "transparent-wrapper.mjs"),
        bridgeCorePath = Path.Combine(root, "bridge-core.mjs"),
        runtimeAuthorityPath = Path.Combine(root, "runtime-process-authority.mjs"),
        childPath = Path.Combine(root, "external-codex", generation, "codex.exe"),
        codeModeHostPath = Path.Combine(root, "external-codex", generation, "codex-code-mode-host.exe"),
        adapterSha256 = new string('a', 64),
        nodeSha256 = new string('b', 64),
        wrapperSha256 = new string('c', 64),
        transparentWrapperSha256 = new string('d', 64),
        bridgeCoreSha256 = new string('e', 64),
        runtimeAuthoritySha256 = new string('f', 64),
        childSha256 = new string('1', 64),
        codeModeHostSha256 = new string('2', 64),
        approvalSinkPath = string.Empty,
        diagnosticsSinkPath = string.Empty
    });

static void WriteState(string path, string manifestPath, string hash, string generation, bool includeUnexpected = false)
{
    object state = includeUnexpected
        ? new
        {
            schema = "k15-codex-bridge/activation-state-v3",
            manifestPath,
            manifestSha256 = hash,
            original = new { },
            runtimeBaseline = new { approvedGeneration = generation, runtimeInventory = Array.Empty<object>() },
            unexpected = "x"
        }
        : new
        {
            schema = "k15-codex-bridge/activation-state-v3",
            manifestPath,
            manifestSha256 = hash,
            original = new { },
            runtimeBaseline = new { approvedGeneration = generation, runtimeInventory = Array.Empty<object>() }
        };
    File.WriteAllText(path, JsonSerializer.Serialize(state));
}

static void WriteDuplicateState(string path, string manifestPath, string hash, string generation)
{
    var json =
        "{\"schema\":\"k15-codex-bridge/activation-state-v3\",\"manifestPath\":" +
        JsonSerializer.Serialize(manifestPath) +
        ",\"manifestPath\":" + JsonSerializer.Serialize(manifestPath) +
        ",\"manifestSha256\":\"" + hash +
        "\",\"original\":{},\"runtimeBaseline\":{\"approvedGeneration\":\"" + generation +
        "\",\"runtimeInventory\":[]}}";
    File.WriteAllText(path, json);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
