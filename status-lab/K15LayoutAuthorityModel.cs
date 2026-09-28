namespace Vorotex.K15.StatusLab;

internal sealed record K15OnboardBindingCell(
    int CellIndex,
    byte[] Raw)
{
    internal byte Family => Raw[0];

    internal bool IsMacro =>
        Raw.Length == K15LayoutAuthorityModel.BindingCellSize &&
        Raw[0] == 0x0A &&
        Raw[1] == 0x00 &&
        Raw[3] == 0x00;

    internal byte? MacroMemorySlot =>
        IsMacro ? Raw[2] : null;

    internal bool IsNative =>
        Raw.Length == K15LayoutAuthorityModel.BindingCellSize &&
        Raw[0] == 0x02 &&
        Raw[2] == 0x00 &&
        Raw[3] == 0x00;

    internal byte? NativeUsage =>
        IsNative ? Raw[1] : null;

    internal bool IsProfileLoop =>
        Raw.AsSpan().SequenceEqual(new byte[] { 0x09, 0x03, 0x00, 0x00 });
}

internal sealed record K15HardwareLayoutSnapshot(
    byte ActiveSlot,
    IReadOnlyDictionary<string, K15OnboardBindingCell> Bindings,
    IReadOnlyDictionary<byte, byte[]> MacroPayloads);

internal static class K15LayoutAuthorityModel
{
    internal const int BindingCellSize = 4;
    internal const int MainBindingCellCount = 160;

    private static readonly IReadOnlyDictionary<string, int> ControlBindingCells =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["key-6"] = 0,
            ["enter"] = 1,
            ["key-5"] = 8,
            ["key-dot"] = 9,
            ["minus"] = 10,
            ["key-4"] = 16,
            ["key-0"] = 17,
            ["plus"] = 18,
            ["key-3"] = 24,
            ["key-9"] = 25,
            ["long-bottom"] = 26,
            ["key-2"] = 32,
            ["key-8"] = 33,
            ["rotary"] = 34,
            ["key-1"] = 40,
            ["key-7"] = 41,
            ["joystick"] = 42
        };

    private static readonly IReadOnlyDictionary<string, string> ControlStorageFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key-1"] = "btn_KBKey_KeyPad1", ["key-2"] = "btn_KBKey_KeyPad2",
            ["key-3"] = "btn_KBKey_KeyPad3", ["key-4"] = "btn_KBKey_KeyPad4",
            ["key-5"] = "btn_KBKey_KeyPad5", ["key-6"] = "btn_KBKey_KeyPad6",
            ["key-7"] = "btn_KBKey_KeyPad7", ["key-8"] = "btn_KBKey_KeyPad8",
            ["key-9"] = "btn_KBKey_KeyPad9", ["key-0"] = "btn_KBKey_KeyPad0",
            ["key-dot"] = "btn_KBKey_KeyPadPoint", ["enter"] = "btn_KBKey_KeyPadEnter",
            ["minus"] = "btn_KBKey_KeyPadSub", ["plus"] = "btn_KBKey_KeyPadAdd",
            ["long-bottom"] = "btn_KBKey_Space", ["joystick"] = "btn_KBKey_Enter",
            ["rotary"] = "btn_KBKey_ProfileLoop"
        };

    internal static IReadOnlyDictionary<string, int> BindingCells => ControlBindingCells;
    internal static IReadOnlyDictionary<string, string> StorageFields => ControlStorageFields;

    internal static int BindingCellForControl(string controlId)
    {
        if (!ControlBindingCells.TryGetValue(controlId, out var cell))
            throw new KeyNotFoundException($"Mini-K15 control '{controlId}' has no proven onboard binding cell.");
        return cell;
    }

    internal static string StorageFieldForControl(string controlId)
    {
        if (!ControlStorageFields.TryGetValue(controlId, out var field))
            throw new KeyNotFoundException($"Mini-K15 control '{controlId}' has no proven profile storage field.");
        return field;
    }

    internal static K15OnboardBindingCell DecodeBindingCell(int cellIndex, ReadOnlySpan<byte> raw)
    {
        if (cellIndex is < 0 or >= MainBindingCellCount)
            throw new ArgumentOutOfRangeException(nameof(cellIndex));
        if (raw.Length != BindingCellSize)
            throw new InvalidDataException(
                $"K15 binding cell {cellIndex} must contain {BindingCellSize} bytes.");

        return new K15OnboardBindingCell(cellIndex, raw.ToArray());
    }
}
