using System.Drawing;

namespace Vorotex.K15.StatusLab;

internal enum PetSizePreset
{
    ExtraSmall,
    Small,
    Medium,
    Large,
    ExtraLarge,
    Huge
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

    private static readonly Rectangle ReferenceKeyboardBody = new(11, 26, 138, 122);
    private static readonly Rectangle ReferenceBadge = new(121, 5, 33, 19);

    internal static string Label(PetSizePreset preset) => Select(preset) switch
    {
        PetSizePreset.ExtraSmall => "Очень маленький",
        PetSizePreset.Small => "Маленький",
        PetSizePreset.Medium => "Средний",
        PetSizePreset.Large => "Большой",
        PetSizePreset.ExtraLarge => "Очень большой",
        PetSizePreset.Huge => "Огромный",
        _ => "Средний"
    };

    internal static CodexPetSizeGeometry Geometry(PetSizePreset preset)
    {
        var size = Select(preset) switch
        {
            PetSizePreset.ExtraSmall => 96,
            PetSizePreset.Small => 128,
            PetSizePreset.Medium => 160,
            PetSizePreset.Large => 192,
            PetSizePreset.ExtraLarge => 256,
            PetSizePreset.Huge => 320,
            _ => 160
        };
        return new(new Size(size, size), Scale(ReferenceKeyboardBody, size), Scale(ReferenceBadge, size));
    }

    private static Rectangle Scale(Rectangle bounds, int size) => new(
        (int)Math.Round(bounds.X * size / 160d),
        (int)Math.Round(bounds.Y * size / 160d),
        (int)Math.Round(bounds.Width * size / 160d),
        (int)Math.Round(bounds.Height * size / 160d));

    internal static Rectangle ControlBounds(Rectangle body, MiniK15Control control) => new(
        body.X + control.X * body.Width / 100,
        body.Y + control.Y * body.Height / 100,
        Math.Max(4, control.Width * body.Width / 100),
        Math.Max(4, control.Height * body.Height / 100));
}
