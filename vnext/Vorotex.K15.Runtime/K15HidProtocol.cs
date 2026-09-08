using System.Collections.Immutable;

namespace Vorotex.K15.Runtime;

/// <summary>Pure vNext-owned K15 protocol facts. It performs no device I/O.</summary>
internal static class K15HidProtocol
{
    public const byte ReportId = 0x06;
    public const int ReportSize = 41;
    public const byte LightingWriteCommand = 0x09;
    public const byte LightingReadCommand = 0x89;
    public const byte DeviceWriteCommand = 0x02;
    public const byte DeviceReadCommand = 0x82;
    public const byte ActiveSlotSelector = 2;
    public const int MaxData = 32;
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
        if (data.Length > MaxData) throw new ArgumentOutOfRangeException(nameof(data));
        var report = new byte[ReportSize];
        report[0] = ReportId; report[2] = 1; report[3] = command; report[4] = sequence;
        report[5] = selector; report[6] = (byte)address; report[7] = (byte)(address >> 8); report[8] = (byte)data.Length;
        data.CopyTo(report.AsSpan(9));
        return report;
    }

    public static byte[] ReadRequest(byte command, byte sequence, byte selector, ushort address, byte length)
    {
        if ((command & 0x80) == 0 || length > MaxData) throw new ArgumentOutOfRangeException(nameof(command));
        return FrameReport(command, sequence, selector, address, new byte[length]);
    }

    public static void ValidateReply(ReadOnlySpan<byte> reply, int expectedLength)
    {
        if (expectedLength <= 0 || reply.Length != expectedLength)
            throw new InvalidDataException("K15 HID reply length is invalid.");
    }

    public static byte[] CreateModeHeader(ReadOnlySpan<byte> originalHeader, byte mode)
    {
        if (originalHeader.Length != LightingRecordSize) throw new ArgumentException("Lighting header must be 25 bytes.");
        var header = originalHeader.ToArray(); header[0] = mode; return header;
    }

    public static byte[] CreateEffectRecord(RuntimeLightingEffect effect, RuntimeWireColorOrder order = RuntimeWireColorOrder.Rgb)
    {
        if (effect.Brightness is < 1 or > 6 || effect.Speed is < 1 or > 7 || effect.Direction is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(effect));
        if (effect.Colors.Length > 7) throw new ArgumentOutOfRangeException(nameof(effect));
        var record = new byte[LightingRecordSize];
        record[0] = (byte)effect.Speed; record[1] = (byte)effect.Direction; record[2] = (byte)(6 - effect.Brightness);
        record[3] = effect.PaletteMask ?? (byte)(effect.Colors.Length == 0 ? 0 : (1 << effect.Colors.Length) - 1);
        for (var i = 0; i < effect.Colors.Length; i++)
        {
            var offset = 4 + i * 3; var color = RuntimeRgbPolicy.ToWire(effect.Colors[i], order);
            record[offset] = color.R; record[offset + 1] = color.G; record[offset + 2] = color.B;
        }
        return record;
    }

    public static ushort ModeRecordAddress(byte mode) => checked((ushort)((mode & 0x3f) * LightingRecordSize));
    public static ImmutableArray<(ushort Address, byte[] Data)> RestorePlan(RuntimeLightingSnapshot snapshot)
    {
        var plan = ImmutableArray.CreateBuilder<(ushort, byte[])>();
        var baseline = snapshot.Header[0];
        if (baseline != OffMode && snapshot.ModeRecords.TryGetValue(baseline, out var baselineRecord))
            plan.Add((ModeRecordAddress(baseline), baselineRecord.ToArray()));
        plan.Add((0, snapshot.Header.ToArray()));
        foreach (var pair in snapshot.ModeRecords.Where(pair => pair.Key != baseline && pair.Key != OffMode).OrderBy(pair => pair.Key))
            plan.Add((ModeRecordAddress(pair.Key), pair.Value.ToArray()));
        return plan.ToImmutable();
    }
    public static bool IsSupportedDevice(ushort vendorId, ushort productId) =>
        vendorId is 0x36A4 or 0xB6A4 && productId is 0x4100 or 0x4101;
}

internal readonly record struct RuntimeRgbColor(byte R, byte G, byte B);
internal sealed record RuntimeLightingEffect(byte Mode, int Brightness, int Speed, int Direction,
    ImmutableArray<RuntimeRgbColor> Colors, byte? PaletteMask = null, byte TargetSlot = 0);
