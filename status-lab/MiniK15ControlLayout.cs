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
    internal static IReadOnlyList<MiniK15Control> Controls { get; } =
    [
        new("rotary", MiniK15ControlBand.Top, MiniK15ControlKind.Rotary, 5, 8, 20, 22),
        new("key-1", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 31, 10, 8, 16, "1"),
        new("key-2", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 41, 10, 8, 16, "2"),
        new("key-3", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 51, 10, 8, 16, "3"),
        new("key-4", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 61, 10, 8, 16, "4"),
        new("key-5", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 71, 10, 8, 16, "5"),
        new("key-6", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 81, 10, 8, 16, "6"),

        new("key-7", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 6, 39, 12, 17, "7"),
        new("key-8", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 20, 39, 12, 17, "8"),
        new("key-9", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 34, 39, 12, 17, "9"),
        new("key-0", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 48, 39, 12, 17, "0"),
        new("key-dot", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 62, 39, 12, 17, "."),
        new("enter", MiniK15ControlBand.Middle, MiniK15ControlKind.WideEnter, 78, 39, 17, 17, "↵"),

        new("minus", MiniK15ControlBand.Bottom, MiniK15ControlKind.Key, 6, 67, 16, 17, "−"),
        new("plus", MiniK15ControlBand.Bottom, MiniK15ControlKind.Key, 25, 67, 16, 17, "+"),
        new("long-bottom", MiniK15ControlBand.Bottom, MiniK15ControlKind.LongBottomKey, 44, 67, 33, 17, "SPACE"),
        new("joystick", MiniK15ControlBand.Bottom, MiniK15ControlKind.Joystick, 81, 65, 15, 21)
    ];
}
