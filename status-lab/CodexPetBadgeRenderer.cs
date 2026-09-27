using System.Drawing.Drawing2D;

namespace Vorotex.K15.StatusLab;

internal static class CodexPetBadgeRenderer
{
    internal static bool ShouldDraw(int count) => count > 0;

    internal static void Draw(Graphics graphics, Rectangle bounds, string countLabel, Color accent)
    {
        if (string.IsNullOrEmpty(countLabel)) return;

        var style = CodexPetBadgeVisualPolicy.Resolve(accent);
        using var fill = new SolidBrush(style.Fill);
        using var outline = new Pen(Color.FromArgb(220, style.Outline), Math.Max(1f, bounds.Width / 33f));
        using var text = new SolidBrush(style.Foreground);
        using var font = new Font("Segoe UI", Math.Clamp(bounds.Height * 0.42f, 7f, 20f),
            FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var radius = Math.Max(3, bounds.Height / 3);
        using var path = RoundedPath(bounds, radius);
        graphics.FillPath(fill, path);
        graphics.DrawPath(outline, path);
        graphics.DrawString(countLabel, font, text, bounds, format);
    }

    private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
