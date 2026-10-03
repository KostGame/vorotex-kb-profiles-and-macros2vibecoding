using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vorotex.K15.StatusLab;

internal sealed record BridgeProvenance(string Identity, string Generation, string Health)
{
    public static BridgeProvenance Unavailable { get; } = new("K15 Codex Bridge", "неизвестно", "UNAVAILABLE");

    public string FormatLine() => $"Bridge: {Identity} / поколение {Generation} / {Health}";
}

internal static class BridgeProvenanceReader
{
    private const string StateSchema = "k15-codex-bridge/activation-state-v3";
    private const string LegacyStateSchema = "k15-codex-bridge/activation-state-v2";
    private const string ManifestSchema = "k15-codex-bridge/production-manifest-v2";
    private const int MaxJsonBytes = 64 * 1024;
    private const int MaxPathLength = 2048;
    private const int MaxGenerationLength = 255;
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GenerationPattern = new("^[A-Za-z0-9._-]{1,255}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IdentityPattern = new("^[A-Za-z0-9._/-]{1,120}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] StateV3Properties =
        ["schema", "manifestPath", "manifestSha256", "original", "runtimeBaseline"];
    private static readonly string[] LegacyStateProperties =
        ["schema", "manifestPath", "original"];
    private static readonly string[] RuntimeBaselineProperties =
        ["approvedGeneration", "runtimeInventory"];
    private static readonly string[] ManifestRequiredProperties =
    [
        "schema",
        "adapterPath", "nodePath", "wrapperPath", "transparentWrapperPath", "bridgeCorePath", "runtimeAuthorityPath", "childPath", "codeModeHostPath",
        "adapterSha256", "nodeSha256", "wrapperSha256", "transparentWrapperSha256", "bridgeCoreSha256", "runtimeAuthoritySha256", "childSha256", "codeModeHostSha256",
        "approvalSinkPath"
    ];
    private const string ManifestOptionalDiagnosticsProperty = "diagnosticsSinkPath";

    public static BridgeProvenance ReadDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return BridgeProvenance.Unavailable;
        var bridgeRoot = Path.Combine(localAppData, "VorotexK15", "vnext", "integration", "codex-bridge");
        return Read(Path.Combine(bridgeRoot, "activation-state.json"), bridgeRoot);
    }

    public static BridgeProvenance Read(string statePath, string bridgeRoot)
    {
        try
        {
            var fullRoot = Path.GetFullPath(bridgeRoot);
            var fullStatePath = Path.GetFullPath(statePath);
            if (!IsWithinRoot(fullRoot, fullStatePath) ||
                !string.Equals(Path.GetFileName(fullStatePath), "activation-state.json", StringComparison.OrdinalIgnoreCase) ||
                !HasNoReparsePoints(fullRoot, fullStatePath))
                return BridgeProvenance.Unavailable;

            var stateBytes = ReadBoundedBytes(fullStatePath);
            using var stateDocument = ParseJson(stateBytes);
            var state = stateDocument.RootElement;
            var schema = GetString(state, "schema");
            if (schema == LegacyStateSchema)
            {
                if (!HasExactProperties(state, LegacyStateProperties) ||
                    !state.TryGetProperty("original", out var legacyOriginal) ||
                    legacyOriginal.ValueKind != JsonValueKind.Object)
                    return BridgeProvenance.Unavailable;
                var legacyManifestPath = GetString(state, "manifestPath");
                if (legacyManifestPath.Length is 0 or > MaxPathLength)
                    return BridgeProvenance.Unavailable;
                return new BridgeProvenance("K15 Codex Bridge", "неизвестно", "LEGACY · NEEDS REVALIDATION");
            }
            if (schema != StateSchema ||
                !HasExactProperties(state, StateV3Properties) ||
                !state.TryGetProperty("original", out var original) ||
                original.ValueKind != JsonValueKind.Object)
                return BridgeProvenance.Unavailable;

            var manifestPath = GetString(state, "manifestPath");
            var expectedHash = GetString(state, "manifestSha256");
            if (manifestPath.Length is 0 or > MaxPathLength || !Path.IsPathFullyQualified(manifestPath) || !Sha256Pattern.IsMatch(expectedHash))
                return BridgeProvenance.Unavailable;

            var fullManifestPath = Path.GetFullPath(manifestPath);
            if (!IsWithinRoot(fullRoot, fullManifestPath) ||
                !string.Equals(Path.GetFileName(fullManifestPath), "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                !HasNoReparsePoints(fullRoot, fullManifestPath))
                return BridgeProvenance.Unavailable;

            if (!state.TryGetProperty("runtimeBaseline", out var baseline) ||
                baseline.ValueKind != JsonValueKind.Object ||
                !HasExactProperties(baseline, RuntimeBaselineProperties) ||
                !baseline.TryGetProperty("runtimeInventory", out var runtimeInventory) ||
                runtimeInventory.ValueKind != JsonValueKind.Array)
                return BridgeProvenance.Unavailable;
            var approvedGeneration = GetString(baseline, "approvedGeneration");
            if (approvedGeneration.Length > MaxGenerationLength || !GenerationPattern.IsMatch(approvedGeneration))
                return BridgeProvenance.Unavailable;

            var manifestBytes = ReadBoundedBytes(fullManifestPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return new BridgeProvenance(GetIdentity(fullRoot, fullManifestPath), approvedGeneration, "NEEDS REVALIDATION · HASH MISMATCH");

            using var manifestDocument = ParseJson(manifestBytes);
            var manifest = manifestDocument.RootElement;
            if (!HasProductionManifestShape(manifest) || GetString(manifest, "schema") != ManifestSchema)
                return BridgeProvenance.Unavailable;
            var childPath = GetString(manifest, "childPath");
            if (childPath.Length is 0 or > MaxPathLength || !Path.IsPathFullyQualified(childPath) ||
                !string.Equals(Path.GetFileName(childPath), "codex.exe", StringComparison.OrdinalIgnoreCase))
                return BridgeProvenance.Unavailable;

            var generationDirectory = Path.GetDirectoryName(Path.GetFullPath(childPath));
            var generation = generationDirectory is null ? string.Empty : Path.GetFileName(generationDirectory);
            if (generation.Length > MaxGenerationLength || !GenerationPattern.IsMatch(generation))
                return BridgeProvenance.Unavailable;

            if (!string.Equals(generation, approvedGeneration, StringComparison.Ordinal))
                return new BridgeProvenance(GetIdentity(fullRoot, fullManifestPath), generation, "NEEDS REVALIDATION · GENERATION MISMATCH");

            return new BridgeProvenance(GetIdentity(fullRoot, fullManifestPath), generation, "CONSISTENT · LIVE HEALTH NOT CHECKED");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return BridgeProvenance.Unavailable;
        }
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            json = bytes.AsMemory(3);
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > MaxJsonBytes)
            throw new IOException("Bridge provenance file size is outside the accepted bound.");
        var buffer = new byte[(int)stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string GetString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException("Bridge provenance field is missing or malformed.");
        return value.GetString() ?? string.Empty;
    }

    private static bool HasExactProperties(JsonElement element, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != expected.Count ||
            properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return false;
        var names = new HashSet<string>(properties.Select(property => property.Name), StringComparer.Ordinal);
        return names.SetEquals(expected);
    }

    private static bool HasProductionManifestShape(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object) return false;
        var properties = manifest.EnumerateObject().ToArray();
        if (properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
            properties.Any(property => property.Value.ValueKind != JsonValueKind.String))
            return false;

        var names = new HashSet<string>(properties.Select(property => property.Name), StringComparer.Ordinal);
        if (names.SetEquals(ManifestRequiredProperties)) return true;

        var withDiagnostics = new HashSet<string>(ManifestRequiredProperties, StringComparer.Ordinal)
        {
            ManifestOptionalDiagnosticsProperty
        };
        return names.SetEquals(withDiagnostics);
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool HasNoReparsePoints(string root, string path)
    {
        if (!Directory.Exists(root)) return false;
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0) return false;

        var relative = Path.GetRelativePath(root, path);
        var current = root;
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) return false;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private static string GetIdentity(string root, string manifestPath)
    {
        var relative = Path.GetRelativePath(root, manifestPath);
        var relativeDirectory = Path.GetDirectoryName(relative);
        var leaf = relativeDirectory is null ? string.Empty : Path.GetFileName(relativeDirectory);
        if (string.IsNullOrEmpty(leaf) || leaf.Length > 120 || !IdentityPattern.IsMatch(leaf))
            return "K15 Codex Bridge";
        return "K15 Codex Bridge · " + leaf;
    }
}
