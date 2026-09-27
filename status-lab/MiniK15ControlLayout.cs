namespace Vorotex.K15.StatusLab;

// Clean-room, normalized layout for the compact K15-inspired screen companion.
// Coordinates are percentages of the chassis so this model remains independent
// from WinForms pixels and can be verified in the smoke suite.
internal enum MiniK15ControlKind
{
    Rotary,
    Key,
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
    internal const int OrdinaryKeyWidth = 10;
    internal const int OrdinaryKeyHeight = 17;

    internal static IReadOnlyList<MiniK15Control> Controls { get; } =
    [
        new("rotary", MiniK15ControlBand.Top, MiniK15ControlKind.Rotary, 5, 8, 20, 22),
        Key("key-1", MiniK15ControlBand.Top, 29, 10, "1"),
        Key("key-2", MiniK15ControlBand.Top, 40, 10, "2"),
        Key("key-3", MiniK15ControlBand.Top, 51, 10, "3"),
        Key("key-4", MiniK15ControlBand.Top, 62, 10, "4"),
        Key("key-5", MiniK15ControlBand.Top, 73, 10, "5"),
        Key("key-6", MiniK15ControlBand.Top, 84, 10, "6"),

        Key("key-7", MiniK15ControlBand.Middle, 8, 39, "7"),
        Key("key-8", MiniK15ControlBand.Middle, 20, 39, "8"),
        Key("key-9", MiniK15ControlBand.Middle, 32, 39, "9"),
        Key("key-0", MiniK15ControlBand.Middle, 44, 39, "0"),
        Key("key-dot", MiniK15ControlBand.Middle, 56, 39, "."),
        new("enter", MiniK15ControlBand.Middle, MiniK15ControlKind.WideEnter, 68, 39, 17, 17, "↵"),

        Key("minus", MiniK15ControlBand.Bottom, 8, 67, "−"),
        Key("plus", MiniK15ControlBand.Bottom, 20, 67, "+"),
        new("long-bottom", MiniK15ControlBand.Bottom, MiniK15ControlKind.LongBottomKey, 34, 67, 33, 17, "SPACE"),
        new("joystick", MiniK15ControlBand.Bottom, MiniK15ControlKind.Joystick, 81, 65, 15, 21)
    ];

    private static MiniK15Control Key(string id, MiniK15ControlBand band, int x, int y, string label) =>
        new(id, band, MiniK15ControlKind.Key, x, y, OrdinaryKeyWidth, OrdinaryKeyHeight, label);
}
