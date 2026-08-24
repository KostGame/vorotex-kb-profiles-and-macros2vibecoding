using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vorotex.K15.StatusLab;

internal sealed class K15HidLightingController : IDisposable
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _handle;
    private byte _sequence;

    private K15HidLightingController(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static K15HidLightingController Open()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");

        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259)
                        break;
                    continue;
                }

                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0)
                    continue;

                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, required, out _, IntPtr.Zero))
                        continue;

                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                        IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    var attr = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attr) ||
                        !K15HidProtocol.IsSupportedDevice(attr.VendorID, attr.ProductID) ||
                        !MatchesVendorConfigurationCollection(handle))
                    {
                        handle.Dispose();
                        continue;
                    }

                    return new K15HidLightingController(handle);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        throw new InvalidOperationException(
            "K15 vendor HID collection B6A4/36A4:4100/4101 FF01:0001 with 41-byte feature report was not found or is busy.");
    }

    public byte ReadActiveSlot()
    {
        var invalidValues = new List<byte>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var data = Query(K15HidProtocol.DeviceReadCommand, K15HidProtocol.ActiveSlotSelector, 0, 1);
            if (data.Length == 1 && data[0] <= 1)
                return data[0];
            if (data.Length == 1)
                invalidValues.Add(data[0]);
            Thread.Sleep(60);
        }

        var detail = invalidValues.Count == 0
            ? "no one-byte slot values"
            : string.Join(", ", invalidValues.Select(value => $"0x{value:X2}"));
        throw new TimeoutException($"K15 active onboard slot did not stabilize after retries ({detail}).");
    }

    public void SelectActiveSlot(byte slot)
    {
        if (slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));

        Write(K15HidProtocol.DeviceWriteCommand, K15HidProtocol.ActiveSlotSelector, 0, new byte[] { slot });
        Thread.Sleep(70);
        var selected = ReadActiveSlot();
        if (selected != slot)
            throw new TimeoutException($"K15 did not settle on requested onboard slot {slot}; observed {selected}.");
    }

    public LightingSnapshot PrepareProfileSnapshot(StatusLabConfig config)
    {
        // Snapshot is rollback authority. Nothing is written before header + touched records are captured.
        var slot = ReadActiveSlot();
        var baselineHeader = ReadLightingHeader();
        var modes = EnumerateModesTouchedByNotifier(config).ToHashSet();
        if (TryModeFromCode(baselineHeader[0], out var baselineMode))
            modes.Add(baselineMode);

        var records = new Dictionary<byte, byte[]>();
        foreach (var mode in modes)
        {
            if (mode == K15LightingMode.Off)
                continue;

            var code = K15HidProtocol.ModeCode(mode);
            var record = Query(K15HidProtocol.LightingReadCommand, 0,
                K15HidProtocol.ModeRecordAddress(mode), K15HidProtocol.LightingRecordSize);
            RequireLength(record, K15HidProtocol.LightingRecordSize, $"{mode} lighting record");
            records[code] = record;
        }

        return new LightingSnapshot(slot, baselineHeader, records);
    }

    public void ApplyEffect(LightingSnapshot snapshot, LightingEffectConfig effect,
        WireColorOrder wireColorOrder, string label)
    {
        RequireSameActiveSlot(snapshot);
        ApplyEffectCore(snapshot.OnboardSlot, snapshot.Header, effect, wireColorOrder, label);
    }

    public void Restore(LightingSnapshot snapshot)
    {
        RequireSameActiveSlot(snapshot);
        var baselineMode = snapshot.Header[0];

        if (snapshot.ModeRecords.TryGetValue(baselineMode, out var baselineRecord) &&
            TryModeFromCode(baselineMode, out var baselineModeEnum))
        {
            WriteAndVerify(K15HidProtocol.LightingWriteCommand, K15HidProtocol.LightingReadCommand, 0,
                K15HidProtocol.ModeRecordAddress(baselineModeEnum), baselineRecord,
                "restore baseline mode record");
        }

        WriteAndVerify(K15HidProtocol.LightingWriteCommand, K15HidProtocol.LightingReadCommand, 0, 0,
            snapshot.Header, "restore lighting header");

        foreach (var pair in snapshot.ModeRecords)
        {
            if (pair.Key == baselineMode || !TryModeFromCode(pair.Key, out var mode))
                continue;
            WriteAndVerify(K15HidProtocol.LightingWriteCommand, K15HidProtocol.LightingReadCommand, 0,
                K15HidProtocol.ModeRecordAddress(mode), pair.Value, $"restore {mode} record");
        }
    }

    private void ApplyEffectCore(byte expectedSlot, ReadOnlySpan<byte> headerTemplate,
        LightingEffectConfig effect, WireColorOrder wireColorOrder, string label)
    {
        var current = ReadActiveSlot();
        if (current != expectedSlot)
            throw new K15ProfileChangedException(expectedSlot, current);

        var header = K15HidProtocol.CreateEffectHeader(headerTemplate, effect);
        if (effect.Mode != K15LightingMode.Off)
        {
            var detail = K15HidProtocol.CreateEffectRecord(effect, wireColorOrder);
            WriteAndVerify(K15HidProtocol.LightingWriteCommand, K15HidProtocol.LightingReadCommand, 0,
                K15HidProtocol.ModeRecordAddress(effect.Mode), detail, $"{label} {effect.Mode} record");
        }

        WriteAndVerify(K15HidProtocol.LightingWriteCommand, K15HidProtocol.LightingReadCommand, 0, 0,
            header, $"{label} lighting header");
    }

    private byte[] ReadLightingHeader()
    {
        var header = Query(K15HidProtocol.LightingReadCommand, 0, 0, K15HidProtocol.LightingRecordSize);
        RequireLength(header, K15HidProtocol.LightingRecordSize, "lighting header");
        return header;
    }

    private static IEnumerable<K15LightingMode> EnumerateModesTouchedByNotifier(StatusLabConfig config)
    {
        yield return config.ProfileSwitch.Mode;
        yield return config.StopSignal.Mode;
        yield return config.ActivationSignal.Mode;
        yield return config.States.Running.Mode;
        yield return config.States.Waiting.Mode;
        yield return config.States.Done.Mode;
        yield return config.States.Error.Mode;
        yield return K15LightingMode.Constant; // Quick Effect Test control.
        yield return K15LightingMode.SingleColorBreathing;
        yield return K15LightingMode.FlowingWater;
        yield return K15LightingMode.CycleBreathing;
    }

    private static bool TryModeFromCode(byte code, out K15LightingMode mode)
    {
        mode = code switch
        {
            K15HidProtocol.ConstantMode => K15LightingMode.Constant,
            K15HidProtocol.FlowingWaterMode => K15LightingMode.FlowingWater,
            K15HidProtocol.MonoWaterMode => K15LightingMode.MonoWater,
            K15HidProtocol.SingleColorBreathingMode => K15LightingMode.SingleColorBreathing,
            K15HidProtocol.CycleBreathingMode => K15LightingMode.CycleBreathing,
            K15HidProtocol.TetrisMode => K15LightingMode.TetrisBlocks,
            K15HidProtocol.NeonMode => K15LightingMode.Neon,
            K15HidProtocol.AmbilightMode => K15LightingMode.Ambilight,
            K15HidProtocol.OffMode => K15LightingMode.Off,
            _ => default
        };
        return code is >= K15HidProtocol.ConstantMode and <= K15HidProtocol.OffMode;
    }

    private void RequireSameActiveSlot(LightingSnapshot snapshot)
    {
        var current = ReadActiveSlot();
        if (current != snapshot.OnboardSlot)
            throw new K15ProfileChangedException(snapshot.OnboardSlot, current);
    }

    private void WriteAndVerify(byte writeCommand, byte readCommand, byte selector, ushort address,
        ReadOnlySpan<byte> data, string label)
    {
        Write(writeCommand, selector, address, data);
        var actual = Query(readCommand, selector, address, (byte)data.Length);
        if (!actual.AsSpan().SequenceEqual(data))
            throw new InvalidDataException($"{label} readback did not match the bytes sent to K15.");
    }

    private void Write(byte command, byte selector, ushort address, ReadOnlySpan<byte> data)
    {
        var report = K15HidProtocol.FrameReport(command, NextSequence(), selector, address, data);
        if (!HidD_SetFeature(_handle, report, report.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"HidD_SetFeature 0x{command:X2} failed.");
        Thread.Sleep(8);
    }

    private byte[] Query(byte command, byte selector, ushort address, byte length)
    {
        for (var requestAttempt = 0; requestAttempt < 3; requestAttempt++)
        {
            var sequence = NextSequence();
            var request = K15HidProtocol.ReadRequest(command, sequence, selector, address, length);
            if (!HidD_SetFeature(_handle, request, request.Length))
            {
                if (requestAttempt == 2)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"HID read request 0x{command:X2} failed.");
                Thread.Sleep(35);
                continue;
            }

            for (var pollAttempt = 0; pollAttempt < 6; pollAttempt++)
            {
                Thread.Sleep(20);
                var response = new byte[K15HidProtocol.ReportSize];
                response[0] = K15HidProtocol.ReportId;
                if (!HidD_GetFeature(_handle, response, response.Length))
                {
                    if (requestAttempt == 2 && pollAttempt == 5)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"HidD_GetFeature 0x{command:X2} failed.");
                    continue;
                }

                var responseLength = response[8];
                if (responseLength is > 0 and <= K15HidProtocol.MaxData &&
                    response[3] == command && response[4] == sequence)
                {
                    return response.AsSpan(9, responseLength).ToArray();
                }
            }
            Thread.Sleep(35);
        }

        throw new TimeoutException($"No matching K15 HID response for command 0x{command:X2} after retries.");
    }

    private byte NextSequence()
    {
        unchecked { _sequence++; }
        return _sequence;
    }

    private static bool MatchesVendorConfigurationCollection(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed) || preparsed == IntPtr.Zero)
            return false;

        try
        {
            var caps = new HidpCaps { Reserved = new ushort[17] };
            var status = HidP_GetCaps(preparsed, ref caps);
            return status >= 0 && caps.UsagePage == 0xFF01 && caps.Usage == 0x0001 &&
                   caps.FeatureReportByteLength == K15HidProtocol.ReportSize;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static void RequireLength(byte[] data, int expected, string label)
    {
        if (data.Length != expected)
            throw new InvalidDataException($"{label} must be {expected} bytes, got {data.Length}.");
    }

    public void Dispose() => _handle.Dispose();

    internal sealed record LightingSnapshot(byte OnboardSlot, byte[] Header,
        IReadOnlyDictionary<byte, byte[]> ModeRecords);

    internal sealed class K15ProfileChangedException : InvalidOperationException
    {
        public byte PreviousSlot { get; }
        public byte CurrentSlot { get; }

        public K15ProfileChangedException(byte previousSlot, byte currentSlot)
            : base($"K15 active profile changed from slot {previousSlot} to {currentSlot}.")
        {
            PreviousSlot = previousSlot;
            CurrentSlot = currentSlot;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
