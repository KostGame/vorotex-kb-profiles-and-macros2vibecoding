using System.Drawing;
using System.Drawing.Drawing2D;

namespace Vorotex.K15.StatusLab;

// Native rounded-region adapter for the presentation-only task surface.
// Layout policy remains GDI-free so reducer smoke projects can reuse it.
internal static class CodexPetTaskSurfaceRegion
{
    internal static Region? Build(TaskSurfaceLayout layout)
    {
        if (layout.Cards.Count == 0) return null;

        var region = new Region(Rectangle.Empty);
        foreach (var card in layout.Cards)
        {
            using var path = RoundedPath(card.Bounds, CodexPetTaskSurfacePolicy.CardCornerRadius);
            region.Union(path);
        }

        if (layout.OverflowCount > 0)
            region.Union(CodexPetTaskSurfacePolicy.OverflowBounds(layout));

        return region;
    }

    internal static bool IsPointInRoundedCard(Rectangle bounds, Point point)
    {
        using var path = RoundedPath(bounds, CodexPetTaskSurfacePolicy.CardCornerRadius);
        return path.IsVisible(point);
    }

    private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
