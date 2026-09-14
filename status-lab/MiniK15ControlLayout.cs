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
        new("rotary", MiniK15ControlBand.Top, MiniK15ControlKind.Rotary, 7, 10, 20, 20),
        new("key-1", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 34, 12, 9, 15, "1"),
        new("key-2", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 44, 12, 9, 15, "2"),
        new("key-3", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 54, 12, 9, 15, "3"),
        new("key-4", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 64, 12, 9, 15, "4"),
        new("key-5", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 74, 12, 9, 15, "5"),
        new("key-6", MiniK15ControlBand.Top, MiniK15ControlKind.Key, 84, 12, 9, 15, "6"),

        new("key-7", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 7, 39, 12, 16, "7"),
        new("key-8", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 21, 39, 12, 16, "8"),
        new("key-9", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 35, 39, 12, 16, "9"),
        new("key-0", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 49, 39, 12, 16, "0"),
        new("key-dot", MiniK15ControlBand.Middle, MiniK15ControlKind.Key, 63, 39, 12, 16, "."),
        new("enter", MiniK15ControlBand.Middle, MiniK15ControlKind.WideEnter, 77, 39, 17, 16, "↵"),

        new("minus", MiniK15ControlBand.Bottom, MiniK15ControlKind.Key, 7, 66, 16, 16, "−"),
        new("plus", MiniK15ControlBand.Bottom, MiniK15ControlKind.Key, 26, 66, 16, 16, "+"),
        new("long-bottom", MiniK15ControlBand.Bottom, MiniK15ControlKind.LongBottomKey, 45, 66, 31, 16),
        new("joystick", MiniK15ControlBand.Bottom, MiniK15ControlKind.Joystick, 81, 64, 15, 20)
    ];
}
