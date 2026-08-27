using System.Text.Json;

namespace Vorotex.K15.VisualTestRig;

public sealed class SettingsStore(string dataRoot)
{
    private readonly string _path = Path.Combine(dataRoot, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public RigSettings Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<RigSettings>(File.ReadAllText(_path), Json);
            return value is null ? RigSettings.Default : value with { Roi = value.Roi.Clamp() };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return RigSettings.Default; }
    }
    public void Save(RigSettings settings)
    {
        Directory.CreateDirectory(dataRoot);
        AtomicFile.Write(_path, JsonSerializer.Serialize(settings with { Roi = settings.Roi.Clamp() }, Json));
    }
}

public static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, true);
    }
}
