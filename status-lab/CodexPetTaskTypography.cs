using System.Drawing;

namespace Vorotex.K15.StatusLab;

internal readonly record struct CodexPetTaskTitleLayout(
    IReadOnlyList<string> Lines,
    bool IsEllipsized);

internal static class CodexPetTaskTypography
{
    internal const float TitlePixelSize = 18f;
    internal const float SubtitlePixelSize = 12f;
    internal const int TitleTopOffset = 6;
    internal const int TitleLineHeight = 22;
    internal const int MaximumTitleLines = 2;
    internal const int TitleHeight = TitleLineHeight * MaximumTitleLines;
    internal const int SubtitleTopOffset = TitleTopOffset + TitleHeight + 1;
    internal const int SubtitleHeight = 22;

    internal static Rectangle TitleBounds(Rectangle cardBounds) =>
        new(cardBounds.Left + 32, cardBounds.Top + TitleTopOffset,
            cardBounds.Width - 42, TitleHeight);

    internal static Rectangle SubtitleBounds(Rectangle cardBounds) =>
        new(cardBounds.Left + 32, cardBounds.Top + SubtitleTopOffset,
            cardBounds.Width - 42, SubtitleHeight);

    internal static CodexPetTaskTitleLayout LayoutTitle(
        Graphics graphics, Font font, string? title, Rectangle bounds)
    {
        var words = (title ?? string.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new([string.Empty], false);

        using var measureFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap
        };
        var width = Math.Max(1, bounds.Width);

        bool Fits(string value) => graphics.MeasureString(value, font,
            PointF.Empty, measureFormat).Width <= width + 0.1f;

        string Ellipsize(string value)
        {
            for (var length = value.Length; length >= 0; length--)
            {
                var candidate = value[..length].TrimEnd() + "…";
                if (Fits(candidate)) return candidate;
            }
            return "…";
        }

        var lines = new List<string>(MaximumTitleLines);
        var current = string.Empty;
        var wordIndex = 0;
        while (wordIndex < words.Length)
        {
            var word = words[wordIndex];
            if (current.Length == 0)
            {
                if (Fits(word))
                {
                    current = word;
                    wordIndex++;
                    continue;
                }

                if (lines.Count == MaximumTitleLines - 1)
                {
                    lines.Add(Ellipsize(word));
                    return new(lines, true);
                }

                var prefixLength = word.Length;
                while (prefixLength > 1 && !Fits(word[..prefixLength])) prefixLength--;
                if (!Fits(word[..prefixLength]))
                {
                    lines.Add(Ellipsize(word));
                    return new(lines, true);
                }

                lines.Add(word[..prefixLength]);
                words[wordIndex] = word[prefixLength..];
                continue;
            }

            var candidate = current + " " + word;
            if (Fits(candidate))
            {
                current = candidate;
                wordIndex++;
                continue;
            }

            lines.Add(current);
            current = string.Empty;
            if (lines.Count == MaximumTitleLines)
            {
                lines[^1] = Ellipsize(lines[^1]);
                return new(lines, true);
            }
        }

        if (current.Length > 0) lines.Add(current);
        return new(lines, false);
    }
}
