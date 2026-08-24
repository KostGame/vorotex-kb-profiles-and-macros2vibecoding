using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Vorotex.K15.Hud;

internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(Color.FromArgb(255, 13, 18, 23));
        graphics.FillEllipse(background, 1, 1, 30, 30);

        using var clipPath = new GraphicsPath();
        clipPath.AddEllipse(2, 2, 28, 28);

        var state = graphics.Save();
        graphics.SetClip(clipPath);
        using (var red = new SolidBrush(Color.FromArgb(95, 214, 76, 78)))
            graphics.FillRectangle(red, 2, 2, 14, 28);
        using (var blue = new SolidBrush(Color.FromArgb(95, 74, 132, 214)))
            graphics.FillRectangle(blue, 16, 2, 14, 28);
        graphics.Restore(state);

        using var redRim = new Pen(Color.FromArgb(255, 214, 76, 78), 2f);
        using var blueRim = new Pen(Color.FromArgb(255, 74, 132, 214), 2f);
        graphics.DrawArc(redRim, 1.5f, 1.5f, 29, 29, 90, 180);
        graphics.DrawArc(blueRim, 1.5f, 1.5f, 29, 29, 270, 180);

        using var vPen = new Pen(Color.FromArgb(255, 242, 246, 250), 4.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(vPen, [new PointF(8, 9), new PointF(16, 23), new PointF(24, 9)]);

        var hIcon = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(hIcon);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }
}
