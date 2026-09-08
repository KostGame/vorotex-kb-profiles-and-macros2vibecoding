namespace Vorotex.K15.Runtime;

/// <summary>Pure vNext-owned K15 protocol facts. It performs no device I/O.</summary>
internal static class K15HidProtocol
{
    public const byte ReportId = 0x06;
    public const int ReportSize = 41;
    public const byte LightingWriteCommand = 0x09;
    public const byte LightingReadCommand = 0x89;
    public const int LightingRecordSize = 25;
    public const byte ConstantMode = 0x81;
    public const byte FlowingWaterMode = 0x82;
    public const byte HorseRaceMode = 0x83;
    public const byte SingleColorBreathingMode = 0x84;
    public const byte CycleBreathingMode = 0x85;
    public const byte TetrisMode = 0x86;
    public const byte NeonMode = 0x87;
    public const byte AmbilightMode = 0x88;
    public const byte OffMode = 0x89;

    public static byte[] FrameReport(byte command, byte sequence, byte selector = 0, ushort address = 0, ReadOnlySpan<byte> data = default)
    {
        if (data.Length > 32) throw new ArgumentOutOfRangeException(nameof(data));
        var report = new byte[ReportSize];
        report[0] = ReportId; report[2] = 1; report[3] = command; report[4] = sequence;
        report[5] = selector; report[6] = (byte)address; report[7] = (byte)(address >> 8); report[8] = (byte)data.Length;
        data.CopyTo(report.AsSpan(9));
        return report;
    }

    public static ushort ModeRecordAddress(byte mode) => checked((ushort)((mode & 0x3f) * LightingRecordSize));
    public static bool IsSupportedDevice(ushort vendorId, ushort productId) =>
        vendorId is 0x36A4 or 0xB6A4 && productId is 0x4100 or 0x4101;
}
