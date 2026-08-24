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

        using var background = new SolidBrush(Color.FromArgb(255, 13, 24, 27));
        using var rim = new Pen(Color.FromArgb(255, 44, 210, 202), 2f);
        graphics.FillEllipse(background, 1, 1, 30, 30);
        graphics.DrawEllipse(rim, 1.5f, 1.5f, 29, 29);

        using var vPen = new Pen(Color.FromArgb(255, 75, 235, 226), 4.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(vPen, [new PointF(8, 9), new PointF(16, 23), new PointF(24, 9)]);

        using var dot = new SolidBrush(Color.FromArgb(255, 246, 180, 37));
        graphics.FillEllipse(dot, 22, 22, 5, 5);

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
