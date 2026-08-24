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
    public const byte SingleColorBreathingMode = 0x84;
    public const int LightingRecordSize = 25;
    public const int SingleColorBreathingRecordIndex = 4;
    public const ushort SingleColorBreathingAddress = SingleColorBreathingRecordIndex * LightingRecordSize;

    public static byte[] FrameReport(
        byte command,
        byte sequence,
        byte selector = 0,
        ushort address = 0,
        ReadOnlySpan<byte> data = default)
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

    public static byte[] ReadRequest(
        byte command,
        byte sequence,
        byte selector = 0,
        ushort address = 0,
        byte length = 0)
    {
        if ((command & 0x80) == 0)
            throw new ArgumentOutOfRangeException(nameof(command), "Read command must have bit 7 set.");
        if (length > MaxData)
            throw new ArgumentOutOfRangeException(nameof(length));

        Span<byte> placeholder = stackalloc byte[length];
        return FrameReport(command, sequence, selector, address, placeholder);
    }

    public static byte[] CreateAlertLightingRecord(
        K15NormalizedState state,
        int brightness = 5,
        int speed = 3)
    {
        if (brightness is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(brightness));
        if (speed is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(speed));

        var rgb = state switch
        {
            K15NormalizedState.Running => (R: (byte)0x00, G: (byte)0x66, B: (byte)0xFF),
            K15NormalizedState.Waiting => (R: (byte)0xFF, G: (byte)0xA5, B: (byte)0x00),
            K15NormalizedState.DonePendingAttention => (R: (byte)0x00, G: (byte)0xFF, B: (byte)0x40),
            K15NormalizedState.Error => (R: (byte)0xFF, G: (byte)0x00, B: (byte)0x00),
            _ => throw new ArgumentOutOfRangeException(nameof(state), "NORMAL is restored from snapshot, not synthesized.")
        };

        var record = new byte[LightingRecordSize];
        record[0] = (byte)speed;
        record[1] = 0;
        record[2] = (byte)(6 - brightness);
        record[3] = 0x01;

        for (var index = 0; index < 7; index++)
        {
            var offset = 4 + index * 3;
            record[offset] = rgb.G;
            record[offset + 1] = rgb.R;
            record[offset + 2] = rgb.B;
        }

        return record;
    }

    public static byte[] CreateAlertHeader(ReadOnlySpan<byte> originalHeader)
    {
        if (originalHeader.Length != LightingRecordSize)
            throw new ArgumentException("Lighting header must be 25 bytes.", nameof(originalHeader));

        var header = originalHeader.ToArray();
        header[0] = SingleColorBreathingMode;
        return header;
    }

    public static bool IsSupportedDevice(ushort vendorId, ushort productId) =>
        (vendorId is 0x36A4 or 0xB6A4) &&
        (productId is 0x4100 or 0x4101);
}
