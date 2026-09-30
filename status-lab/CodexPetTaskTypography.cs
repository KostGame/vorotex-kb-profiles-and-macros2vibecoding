namespace Vorotex.K15.StatusLab;

internal static class CodexPetTaskTypography
{
    internal const float TitlePixelSize = 18f;
    internal const float SubtitlePixelSize = 12f;
    internal const int TitleTopOffset = 6;
    internal const int TitleHeight = 29;
    internal const int SubtitleTopOffset = 39;
    internal const int SubtitleHeight = 22;

    internal static Rectangle TitleBounds(Rectangle cardBounds) =>
        new(cardBounds.Left + 32, cardBounds.Top + TitleTopOffset,
            cardBounds.Width - 42, TitleHeight);

    internal static Rectangle SubtitleBounds(Rectangle cardBounds) =>
        new(cardBounds.Left + 32, cardBounds.Top + SubtitleTopOffset,
            cardBounds.Width - 42, SubtitleHeight);
}
