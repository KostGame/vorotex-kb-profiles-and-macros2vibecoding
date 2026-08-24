namespace Vorotex.K15.StatusLab;

internal static class K15HidProtocol
{
    public const byte ReportId = 0x06;
    public const int ReportSize = 41;
    public const int MaxData = 32;
    public const byte LightingWriteCommand = 0x09;
    public const byte LightingReadCommand = 0x89;
    public const byte DeviceWriteCommand = 0x02;
    public const byte DeviceReadCommand = 0x82;
    public const byte ActiveSlotSelector = 2;

    public const byte ConstantMode = 0x81;
    public const byte FlowingWaterMode = 0x82;
    public const byte HorseRaceMode = 0x83;
    public const byte MonoWaterMode = HorseRaceMode; // historical Status Lab name
    public const byte SingleColorBreathingMode = 0x84;
    public const byte CycleBreathingMode = 0x85;
    public const byte TetrisMode = 0x86;
    public const byte NeonMode = 0x87;
    public const byte AmbilightMode = 0x88;
    public const byte OffMode = 0x89;

    public const int LightingRecordSize = 25;

    public static byte[] FrameReport(byte command, byte sequence, byte selector = 0,
        ushort address = 0, ReadOnlySpan<byte> data = default)
    {
        if (data.Length > MaxData)
            throw new ArgumentOutOfRangeException(nameof(data), "Payload exceeds 32 bytes.");

        var report = new byte[ReportSize];
        report[0] = ReportId;
        report[1] = 0x00;
        report[2] = 0x01;
        report[3] = command;
        report[4] = sequence;
        report[5] = selector;
        report[6] = (byte)(address & 0xff);
        report[7] = (byte)(address >> 8);
        report[8] = (byte)data.Length;
        data.CopyTo(report.AsSpan(9));
        return report;
    }

    public static byte[] ReadRequest(byte command, byte sequence, byte selector = 0,
        ushort address = 0, byte length = 0)
    {
        if ((command & 0x80) == 0)
            throw new ArgumentOutOfRangeException(nameof(command), "Read command must have bit 7 set.");
        if (length > MaxData)
            throw new ArgumentOutOfRangeException(nameof(length));
        Span<byte> placeholder = stackalloc byte[length];
        return FrameReport(command, sequence, selector, address, placeholder);
    }

    public static byte ModeCode(K15LightingMode mode) => mode switch
    {
        K15LightingMode.Constant => ConstantMode,
        K15LightingMode.FlowingWater => FlowingWaterMode,
        K15LightingMode.MonoWater => HorseRaceMode,
        K15LightingMode.SingleColorBreathing => SingleColorBreathingMode,
        K15LightingMode.CycleBreathing => CycleBreathingMode,
        K15LightingMode.TetrisBlocks => TetrisMode,
        K15LightingMode.Neon => NeonMode,
        K15LightingMode.Ambilight => AmbilightMode,
        K15LightingMode.Off => OffMode,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static ushort ModeRecordAddress(K15LightingMode mode)
    {
        var recordIndex = ModeCode(mode) & 0x3f;
        return checked((ushort)(recordIndex * LightingRecordSize));
    }

    public static byte[] CreateEffectHeader(ReadOnlySpan<byte> originalHeader, LightingEffectConfig effect) =>
        CreateModeHeader(originalHeader, ModeCode(effect.Mode));

    public static byte[] CreateEffectRecord(LightingEffectConfig effect, WireColorOrder wireColorOrder)
    {
        if (effect.Brightness is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(effect.Brightness));
        if (effect.Speed is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(effect.Speed));
        if (effect.Direction is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(effect.Direction));
        if (effect.Colors.Length > 7)
            throw new ArgumentOutOfRangeException(nameof(effect.Colors));
        if (effect.PaletteMask is byte explicitMask && explicitMask > 0x7f)
            throw new ArgumentOutOfRangeException(nameof(effect.PaletteMask));

        var record = new byte[LightingRecordSize];
        record[0] = (byte)effect.Speed;
        record[1] = (byte)effect.Direction;
        record[2] = (byte)(6 - effect.Brightness);

        var colors = effect.Colors.Take(7).Select(StatusLabConfig.ParseColor).ToArray();
        if (effect.Mode != K15LightingMode.Off && colors.Length == 0)
            throw new InvalidDataException("Lighting effect requires at least one color/seed record.");

        record[3] = effect.PaletteMask ?? (colors.Length == 0 ? (byte)0 : (byte)((1 << colors.Length) - 1));

        for (var index = 0; index < colors.Length; index++)
        {
            var offset = 4 + index * 3;
            var color = colors[index];
            if (wireColorOrder == WireColorOrder.RGB)
            {
                record[offset] = color.R;
                record[offset + 1] = color.G;
                record[offset + 2] = color.B;
            }
            else
            {
                record[offset] = color.G;
                record[offset + 1] = color.R;
                record[offset + 2] = color.B;
            }
        }
        return record;
    }

    public static byte[] CreateModeHeader(ReadOnlySpan<byte> originalHeader, byte mode)
    {
        if (originalHeader.Length != LightingRecordSize)
            throw new ArgumentException("Lighting header must be 25 bytes.", nameof(originalHeader));
        var header = originalHeader.ToArray();
        header[0] = mode;
        return header;
    }

    public static byte[] CreateConstantHeader(ReadOnlySpan<byte> originalHeader) =>
        CreateModeHeader(originalHeader, ConstantMode);

    public static bool IsNotifierMode(byte mode) =>
        mode is ConstantMode or FlowingWaterMode or SingleColorBreathingMode or OffMode;

    public static bool IsSupportedDevice(ushort vendorId, ushort productId) =>
        (vendorId is 0x36A4 or 0xB6A4) && (productId is 0x4100 or 0x4101);
}
