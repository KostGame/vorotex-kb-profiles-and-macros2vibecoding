using System.Drawing;

namespace Vorotex.K15.StatusLab;

// Small pure policy seams keep popup behavior deterministic without adding
// global input hooks or coupling tests to WinForms event ordering.
internal static class CodexPetPopupPolicy
{
    internal static bool ToggleOpen(bool isOpen) => !isOpen;
    internal static int VisibleRows(int total) => Math.Min(5, Math.Max(0, total));
    internal static int OverflowCount(int total) => Math.Max(0, total - VisibleRows(total));

    internal static Point ClampToWorkingArea(Point desired, Size popupSize, Rectangle workingArea) => new(
        Math.Clamp(desired.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - popupSize.Width)),
        Math.Clamp(desired.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - popupSize.Height)));

    internal static Color Accent(PetPalette palette, CodexPetVisualState state)
    {
        var factor = state switch
        {
            CodexPetVisualState.Waiting => 1.15d,
            CodexPetVisualState.Review => 1.0d,
            CodexPetVisualState.Running => 0.88d,
            _ => 1.0d
        };
        var baseColor = palette.Primary;
        return Color.FromArgb(Clamp(baseColor.R * factor), Clamp(baseColor.G * factor), Clamp(baseColor.B * factor));
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
