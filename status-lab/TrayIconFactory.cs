using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Vorotex.K15.StatusLab;

internal static class TrayIconFactory
{
    public static Icon Create(bool trackingEnabled)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 17, 21, 27));
            using var border = new Pen(Color.FromArgb(255, 58, 67, 82), 1.5f);
            graphics.FillRoundedRectangle(background, new RectangleF(1.5f, 1.5f, 29f, 29f), 7f);
            graphics.DrawRoundedRectangle(border, new RectangleF(1.5f, 1.5f, 29f, 29f), 7f);

            var markColor = trackingEnabled
                ? Color.FromArgb(255, 194, 255, 60)
                : Color.FromArgb(255, 130, 139, 153);
            using var markPen = new Pen(markColor, 4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLines(markPen,
            [
                new PointF(8f, 9f),
                new PointF(15.5f, 23f),
                new PointF(23f, 9f)
            ]);

            var dotColor = trackingEnabled
                ? Color.FromArgb(255, 194, 255, 60)
                : Color.FromArgb(255, 72, 79, 91);
            using var dot = new SolidBrush(dotColor);
            graphics.FillEllipse(dot, 22f, 4f, 6f, 6f);
            if (trackingEnabled)
            {
                using var glow = new Pen(Color.FromArgb(180, 194, 255, 60), 1.5f);
                graphics.DrawEllipse(glow, 21f, 3f, 8f, 8f);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rectangle, float radius)
    {
        using var path = RoundedRectangle(rectangle, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF rectangle, float radius)
    {
        using var path = RoundedRectangle(rectangle, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
