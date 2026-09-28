namespace Vorotex.K15.StatusLab;

internal static class CodexPetControlHitTest
{
    internal static MiniK15Control? HitTest(Rectangle keyboardBody, Point point)
    {
        foreach (var control in MiniK15ControlLayout.Controls)
        {
            var bounds = CodexPetSizePolicy.ControlBounds(keyboardBody, control);
            if (Contains(control, bounds, point))
                return control;
        }

        return null;
    }

    private static bool Contains(MiniK15Control control, Rectangle bounds, Point point)
    {
        if (!bounds.Contains(point))
            return false;

        if (control.Kind is not (MiniK15ControlKind.Rotary or MiniK15ControlKind.Joystick))
            return true;

        var radiusX = bounds.Width / 2d;
        var radiusY = bounds.Height / 2d;
        if (radiusX <= 0 || radiusY <= 0)
            return false;

        var centerX = bounds.Left + radiusX;
        var centerY = bounds.Top + radiusY;
        var dx = (point.X - centerX) / radiusX;
        var dy = (point.Y - centerY) / radiusY;
        return dx * dx + dy * dy <= 1d;
    }
}