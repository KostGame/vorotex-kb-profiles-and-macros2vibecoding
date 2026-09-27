using System.Drawing;

namespace Vorotex.K15.StatusLab;

internal readonly record struct CodexPetBadgeStyle(Color Fill, Color Outline, Color Foreground, double ContrastRatio);

internal static class CodexPetBadgeVisualPolicy
{
    private static readonly Color ChassisSurface = Color.FromArgb(40, 45, 54);
    private static readonly Color White = Color.White;
    private static readonly Color Black = Color.Black;

    internal static CodexPetBadgeStyle Resolve(Color accent)
    {
        var fill = Blend(ChassisSurface, accent, 0.38);
        var whiteContrast = ContrastRatio(White, fill);
        var blackContrast = ContrastRatio(Black, fill);
        var foreground = whiteContrast >= blackContrast ? White : Black;
        var contrast = Math.Max(whiteContrast, blackContrast);
        return new CodexPetBadgeStyle(fill, Blend(accent, White, 0.42), foreground, contrast);
    }

    internal static double ContrastRatio(Color foreground, Color background)
    {
        var foregroundLuminance = RelativeLuminance(foreground);
        var backgroundLuminance = RelativeLuminance(background);
        var lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        var darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color Blend(Color start, Color end, double amount) => Color.FromArgb(
        (int)Math.Round(start.R + (end.R - start.R) * amount),
        (int)Math.Round(start.G + (end.G - start.G) * amount),
        (int)Math.Round(start.B + (end.B - start.B) * amount));
}
