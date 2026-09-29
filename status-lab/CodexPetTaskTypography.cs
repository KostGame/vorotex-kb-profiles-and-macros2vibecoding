namespace Vorotex.K15.StatusLab;

internal static class CodexPetTaskTypography
{
    internal const float TitlePixelSize = 14f;
    internal const float SubtitlePixelSize = 9f;
    internal const int TitleTopOffset = 5;
    internal const int TitleHeight = 23;
    internal const int SubtitleTopOffset = 30;
    internal const int SubtitleHeight = 17;

    internal static Rectangle TitleBounds(Rectangle cardBounds) =>
        new(cardBounds.Left + 32, cardBounds.Top + TitleTopOffset,
            cardBounds.Width - 42, TitleHeight);

    internal static Rectangle SubtitleBounds(Rectangle cardBounds) =>
        new(cardBounds.Left + 32, cardBounds.Top + SubtitleTopOffset,
            cardBounds.Width - 42, SubtitleHeight);
}
