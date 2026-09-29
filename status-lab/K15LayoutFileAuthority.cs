using System.Security.Cryptography;
using System.Text;

namespace Vorotex.K15.StatusLab;

internal sealed record K15LayoutFilePaths(
    string Profile0,
    string Profile1,
    string MacroConfig)
{
    internal string Profile(byte slot) => slot switch
    {
        0 => Profile0,
        1 => Profile1,
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };

    internal static K15LayoutFilePaths ResolveDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "VOROTEX-K15-PRO", "res");
        return new K15LayoutFilePaths(
            Path.Combine(root, "KeyboardDock", "KeyboardA", "Config", "Profile0.json"),
            Path.Combine(root, "KeyboardDock", "KeyboardA", "Config", "Profile1.json"),
            Path.Combine(root, "MacroDock", "MacroData", "macroConfig.json"));
    }
}

internal sealed record K15LayoutFileStamp(
    byte Slot,
    string ProfileSha256,
    string MacroConfigSha256);

internal sealed record K15LocalLayoutRead(
    K15LocalSemanticSnapshot Semantic,
    K15LayoutFileStamp Stamp);

internal static class K15LayoutFileAuthority
{
    internal static K15LocalLayoutRead Read(K15LayoutFilePaths paths, byte slot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var profileBytes = ReadRequired(paths.Profile(slot));
        var macroBytes = ReadRequired(paths.MacroConfig);

        var profileJson = DecodeJson(profileBytes);
        var macroJson = DecodeJson(macroBytes);
        var semantic = K15LocalLayoutSemanticModel.Parse(profileJson, macroJson, slot);
        var stamp = new K15LayoutFileStamp(
            slot,
            Sha256(profileBytes),
            Sha256(macroBytes));
        return new K15LocalLayoutRead(semantic, stamp);
    }

    internal static bool Matches(K15LayoutFilePaths paths, K15LayoutFileStamp expected)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            return string.Equals(
                       Sha256(ReadRequired(paths.Profile(expected.Slot))),
                       expected.ProfileSha256,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       Sha256(ReadRequired(paths.MacroConfig)),
                       expected.MacroConfigSha256,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static byte[] ReadRequired(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required VOROTEX layout file is missing.", path);
        return File.ReadAllBytes(path);
    }

    private static string DecodeJson(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
