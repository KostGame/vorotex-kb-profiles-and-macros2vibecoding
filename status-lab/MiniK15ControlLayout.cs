namespace Vorotex.K15.StatusLab;

// Clean-room, normalized layout for the compact K15-inspired screen companion.
// Coordinates are percentages of the chassis so this model remains independent
// from WinForms pixels and can be verified in the smoke suite.
internal enum MiniK15ControlKind
{
    Rotary,
    SquareKey,
    RectangularKey,
    WideEnter,
    LongBottomKey,
    Joystick
}

internal enum MiniK15ControlBand { Top, Middle, Bottom }

internal sealed record MiniK15Control(
    string Id,
    MiniK15ControlBand Band,
    MiniK15ControlKind Kind,
    int X,
    int Y,
    int Width,
    int Height,
    string? Label = null);

internal static class MiniK15ControlLayout
{
    internal const int OrdinaryKeyWidth = 12;
    internal const int OrdinaryKeyHeight = 18;
    internal const int ModifierKeyWidth = 10;
    internal const int RotaryWidth = 17;
    internal const int JoystickWidth = 17;
    internal const int EnterWidth = 29;

    internal const int ChassisWidth = 100;
    internal const int RowGutter = 1;
    internal const int BottomOuterMargin = 3;
    internal const int BottomJoystickGap = 3;
    internal const int SpaceWidth = ChassisWidth - 2 * BottomOuterMargin - JoystickWidth - BottomJoystickGap -
        2 * ModifierKeyWidth - 2 * RowGutter;

    private sealed record Track(string Id, MiniK15ControlKind Kind, int Width, string? Label = null);

    private static readonly MiniK15Control[] TopRow = ComposeCenteredRow(MiniK15ControlBand.Top, 12,
    [
        new("rotary", MiniK15ControlKind.Rotary, RotaryWidth),
        new("key-1", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "1"),
        new("key-2", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "2"),
        new("key-3", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "3"),
        new("key-4", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "4"),
        new("key-5", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "5"),
        new("key-6", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "6")
    ]);

    private static readonly MiniK15Control[] MiddleRow = ComposeCenteredRow(MiniK15ControlBand.Middle, 38,
    [
        new("key-7", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "7"),
        new("key-8", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "8"),
        new("key-9", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "9"),
        new("key-0", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "0"),
        new("key-dot", MiniK15ControlKind.SquareKey, OrdinaryKeyWidth, "."),
        new("enter", MiniK15ControlKind.WideEnter, EnterWidth, "↵")
    ]);

    private static readonly MiniK15Control[] BottomRow = ComposeBottomRow();

    internal static IReadOnlyList<MiniK15Control> Controls { get; } =
        TopRow.Concat(MiddleRow).Concat(BottomRow).ToArray();

    private static MiniK15Control[] ComposeCenteredRow(MiniK15ControlBand band, int y, IReadOnlyList<Track> tracks)
    {
        var rowWidth = tracks.Sum(track => track.Width) + RowGutter * (tracks.Count - 1);
        return ComposeRow(band, y, tracks, (ChassisWidth - rowWidth) / 2);
    }

    private static MiniK15Control[] ComposeRow(MiniK15ControlBand band, int y, IReadOnlyList<Track> tracks, int x)
    {
        var controls = new MiniK15Control[tracks.Count];
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            controls[index] = new MiniK15Control(track.Id, band, track.Kind, x, y, track.Width,
                OrdinaryKeyHeight, track.Label);
            x += track.Width + RowGutter;
        }
        return controls;
    }

    private static MiniK15Control[] ComposeBottomRow()
    {
        Track[] leftTracks =
        [
            new("minus", MiniK15ControlKind.RectangularKey, ModifierKeyWidth, "−"),
            new("plus", MiniK15ControlKind.RectangularKey, ModifierKeyWidth, "+"),
            new("long-bottom", MiniK15ControlKind.LongBottomKey, SpaceWidth, "SPACE")
        ];
        var leftWidth = leftTracks.Sum(track => track.Width) + RowGutter * (leftTracks.Length - 1);
        var leftCluster = ComposeRow(MiniK15ControlBand.Bottom, 64, leftTracks, BottomOuterMargin);

        // Keep joystick in its own right cell. The left cluster and outer edge
        // share fixed row tokens, so the separation stays deliberate at all sizes.
        var joystickX = BottomOuterMargin + leftWidth + BottomJoystickGap;
        var joystick = new MiniK15Control("joystick", MiniK15ControlBand.Bottom,
            MiniK15ControlKind.Joystick, joystickX, 64, JoystickWidth, OrdinaryKeyHeight);
        return [.. leftCluster, joystick];
    }
}
