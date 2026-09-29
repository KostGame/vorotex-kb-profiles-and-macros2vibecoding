using System.Drawing.Drawing2D;

namespace Vorotex.K15.StatusLab;

internal static class CodexPetControlRenderer
{
    internal static void Draw(
        Graphics graphics,
        MiniK15Control control,
        Rectangle bounds,
        Color fillColor,
        CodexPetControlRenderState interaction)
    {
        if (interaction.Pressed)
        {
            bounds.Offset(0, Math.Max(1, bounds.Height / 12));
            fillColor = Blend(fillColor, Color.Black, 0.22d);
        }
        else if (interaction.Hovered)
        {
            fillColor = Blend(fillColor, Color.White, 0.10d);
        }

        using var controlFill = new SolidBrush(fillColor);
        using var controlOutline = new Pen(
            OutlineColor(interaction),
            OutlineWidth(bounds, interaction));

        if (control.Kind is MiniK15ControlKind.Rotary or MiniK15ControlKind.Joystick)
        {
            bounds = CodexPetControlVisualPolicy.CircleBounds(bounds);
            graphics.FillEllipse(controlFill, bounds);
            graphics.DrawEllipse(controlOutline, bounds);
            using var detail = new Pen(Color.FromArgb(145, 180, 190, 198),
                CodexPetControlVisualPolicy.RingPenWidth(bounds.Width));
            graphics.DrawEllipse(detail, CodexPetControlVisualPolicy.InnerRingBounds(bounds));
            if (control.Kind == MiniK15ControlKind.Joystick)
            {
                var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                var arm = Math.Max(4, bounds.Width / 5);
                graphics.DrawLine(detail, center.X - arm, center.Y, center.X + arm, center.Y);
                graphics.DrawLine(detail, center.X, center.Y - arm, center.X, center.Y + arm);
            }
            return;
        }

        var radius = control.Kind == MiniK15ControlKind.LongBottomKey ? 4 : 3;
        FillRounded(graphics, bounds, radius, controlFill);
        DrawRounded(graphics, bounds, radius, controlOutline);
        if (!string.IsNullOrWhiteSpace(control.Label))
        {
            using var label = new SolidBrush(Color.FromArgb(215, 235, 240, 244));
            using var font = new Font("Segoe UI", CodexPetControlVisualPolicy.LabelFontSize(control, bounds),
                FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString(control.Label, font, label, bounds, format);
        }
    }

    private static Color OutlineColor(CodexPetControlRenderState state)
    {
        var alpha = (int)Math.Round(150 + 105 * Math.Clamp(state.Pulse, 0d, 1d));
        return state.FeedbackPhase switch
        {
            CodexPetControlFeedbackPhase.Dispatching => Color.FromArgb(alpha, 82, 196, 255),
            CodexPetControlFeedbackPhase.Switching => Color.FromArgb(alpha, 255, 196, 78),
            CodexPetControlFeedbackPhase.Reattesting => Color.FromArgb(alpha, 255, 221, 112),
            CodexPetControlFeedbackPhase.Success => Color.FromArgb(245, 82, 224, 142),
            CodexPetControlFeedbackPhase.Blocked => Color.FromArgb(245, 255, 96, 88),
            _ when state.Pressed => Color.FromArgb(235, 246, 250, 252),
            _ when state.Hovered => Color.FromArgb(185, 210, 230, 242),
            _ => Color.FromArgb(24, 28, 35)
        };
    }

    private static float OutlineWidth(Rectangle bounds, CodexPetControlRenderState state)
    {
        if (state.FeedbackPhase != CodexPetControlFeedbackPhase.None || state.Pressed)
            return Math.Max(1.5f, bounds.Width / 14f);
        return state.Hovered ? Math.Max(1.25f, bounds.Width / 20f) : 1f;
    }

    private static Color Blend(Color value, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        static int Channel(byte from, byte to, double a) =>
            (int)Math.Round(from + (to - from) * a);
        return Color.FromArgb(
            value.A,
            Channel(value.R, target.R, amount),
            Channel(value.G, target.G, amount),
            Channel(value.B, target.B, amount));
    }

    private static void FillRounded(Graphics graphics, Rectangle rectangle, int radius, Brush brush)
    {
        using var path = RoundedPath(rectangle, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRounded(Graphics graphics, Rectangle rectangle, int radius, Pen pen)
    {
        using var path = RoundedPath(rectangle, radius);
        graphics.DrawPath(pen, path);
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
