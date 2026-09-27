using System.Drawing;

namespace Vorotex.K15.StatusLab;

internal static class CodexPetControlVisualPolicy
{
    internal static Rectangle CircleBounds(Rectangle bounds)
    {
        var side = Math.Min(bounds.Width, bounds.Height);
        return new Rectangle((int)Math.Round(bounds.Left + bounds.Width / 2d - side / 2d),
            (int)Math.Round(bounds.Top + bounds.Height / 2d - side / 2d), side, side);
    }

    internal static int RingInset(int side) => Math.Clamp((int)Math.Round(side * 0.18), 1,
        Math.Max(1, (side - 3) / 2));

    internal static Rectangle InnerRingBounds(Rectangle circle)
    {
        var inset = RingInset(circle.Width);
        return Rectangle.Inflate(circle, -inset, -inset);
    }

    internal static float RingPenWidth(int side) => Math.Max(1f, (float)Math.Round(side * 0.045));

    internal static float LabelFontSize(MiniK15Control control, Rectangle bounds)
    {
        if (string.IsNullOrEmpty(control.Label)) return 0f;

        var usableWidth = Math.Max(1, bounds.Width - 2);
        var usableHeight = Math.Max(1, bounds.Height - 2);
        var heightRatio = control.Kind switch
        {
            MiniK15ControlKind.SquareKey => 0.76f,
            MiniK15ControlKind.RectangularKey => 0.60f,
            MiniK15ControlKind.WideEnter => 0.54f,
            MiniK15ControlKind.LongBottomKey => 0.52f,
            _ => 0.5f
        };
        var widthRatio = control.Kind == MiniK15ControlKind.LongBottomKey ? 0.62f : 0.70f;
        var heightLimit = usableHeight * heightRatio;
        var widthLimit = usableWidth / (control.Label.Length * widthRatio);
        return Math.Min(28f, Math.Min(heightLimit, widthLimit));
    }
}
