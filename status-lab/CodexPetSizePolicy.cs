using System.Drawing;

namespace Vorotex.K15.StatusLab;

internal enum PetSizePreset
{
    Small,
    Medium,
    Large
}

internal readonly record struct CodexPetSizeGeometry(
    Size WindowSize,
    Rectangle KeyboardBodyBounds,
    Rectangle BadgeBounds);

internal static class CodexPetSizePolicy
{
    internal const PetSizePreset DefaultPreset = PetSizePreset.Medium;

    internal static PetSizePreset Select(PetSizePreset preset) =>
        Enum.IsDefined(preset) ? preset : DefaultPreset;

    internal static CodexPetSizeGeometry Geometry(PetSizePreset preset) => Select(preset) switch
    {
        PetSizePreset.Small => new(new Size(128, 128), new Rectangle(9, 22, 111, 97), new Rectangle(96, 3, 29, 18)),
        PetSizePreset.Large => new(new Size(192, 192), new Rectangle(13, 31, 166, 146), new Rectangle(145, 6, 40, 23)),
        _ => new(new Size(160, 160), new Rectangle(11, 26, 138, 122), new Rectangle(121, 5, 33, 19))
    };

    internal static Rectangle ControlBounds(Rectangle body, MiniK15Control control) => new(
        body.X + control.X * body.Width / 100,
        body.Y + control.Y * body.Height / 100,
        Math.Max(4, control.Width * body.Width / 100),
        Math.Max(4, control.Height * body.Height / 100));
}
