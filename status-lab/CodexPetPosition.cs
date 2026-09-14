using System.Drawing;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal readonly record struct CodexPetPosition(int X, int Y)
{
    internal Point ToPoint() => new(X, Y);
}

internal static class CodexPetPositionPolicy
{
    internal const int MinimumVisiblePixels = 24;

    internal static Point Resolve(CodexPetPosition? saved, Size petSize, IReadOnlyList<Rectangle> workingAreas)
    {
        if (saved is { } position && IsRecoverable(position.ToPoint(), petSize, workingAreas))
            return position.ToPoint();

        var area = workingAreas.FirstOrDefault(candidate => candidate.Contains(0, 0));
        if (area == Rectangle.Empty)
            area = workingAreas.FirstOrDefault();
        if (area == Rectangle.Empty)
            return Point.Empty;

        return new Point(
            Math.Max(area.Left, area.Right - petSize.Width - 24),
            Math.Max(area.Top, area.Bottom - petSize.Height - 24));
    }

    internal static bool IsRecoverable(Point location, Size petSize, IReadOnlyList<Rectangle> workingAreas)
    {
        var pet = new Rectangle(location, petSize);
        return workingAreas.Any(area =>
        {
            var visible = Rectangle.Intersect(pet, area);
            return visible.Width >= MinimumVisiblePixels && visible.Height >= MinimumVisiblePixels;
        });
    }
}

internal sealed class CodexPetPositionStore
{
    private sealed record PersistedPosition(int X, int Y);

    internal string FilePath { get; }

    internal CodexPetPositionStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VOROTEX", "K15 Status Lab", "codex-pet-position.json");
    }

    internal CodexPetPosition? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var position = JsonSerializer.Deserialize<PersistedPosition>(File.ReadAllText(FilePath));
            return position is null ? null : new CodexPetPosition(position.X, position.Y);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Save(CodexPetPosition position)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new PersistedPosition(position.X, position.Y)));
        File.Move(temporaryPath, FilePath, true);
    }

    internal static string Serialize(CodexPetPosition position) =>
        JsonSerializer.Serialize(new PersistedPosition(position.X, position.Y));

    internal static CodexPetPosition? Deserialize(string json)
    {
        try
        {
            var position = JsonSerializer.Deserialize<PersistedPosition>(json);
            return position is null ? null : new CodexPetPosition(position.X, position.Y);
        }
        catch (JsonException)
        {
            return null;
        }
    }

}
