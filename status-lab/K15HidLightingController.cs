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
        var set = SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed.");

        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData
                {
                    cbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259)
                        break;
                    continue;
                }

                SetupDiGetDeviceInterfaceDetail(
                    set,
                    ref iface,
                    IntPtr.Zero,
                    0,
                    out var required,
                    IntPtr.Zero);

                if (required == 0)
                    continue;

                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(
                            set,
                            ref iface,
                            buffer,
                            required,
                            out _,
                            IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    var handle = CreateFile(
                        path,
                        GenericRead | GenericWrite,
                        FileShareRead | FileShareWrite,
                        IntPtr.Zero,
                        OpenExisting,
                        0,
                        IntPtr.Zero);

                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    var attr = new HiddAttributes
                    {
                        Size = Marshal.SizeOf<HiddAttributes>()
                    };

                    if (!HidD_GetAttributes(handle, ref attr) ||
                        !K15HidProtocol.IsSupportedDevice(attr.VendorID, attr.ProductID))
                    {
                        handle.Dispose();
                        continue;
                    }

                    if (!MatchesVendorConfigurationCollection(handle))
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

        // During a hardware profile switch the device can briefly return a value outside 0/1.
        // Treat that as a transition and retry instead of crashing the WinForms async handler.
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

        // This is the same bounded slot-select command used by the open W910 driver before
        // reading/writing a profile. Status Lab uses it only to clean a notifier overlay from the
        // profile the owner just switched away from, then immediately returns to the owner's slot.
        Write(
            K15HidProtocol.DeviceWriteCommand,
            K15HidProtocol.ActiveSlotSelector,
            0,
            new byte[] { slot });
        Thread.Sleep(70);

        var selected = ReadActiveSlot();
        if (selected != slot)
            throw new TimeoutException($"K15 did not settle on requested onboard slot {slot}; observed {selected}.");
    }

    public LightingSnapshot CaptureLightingSnapshot()
    {
        var slot = ReadActiveSlot();
        var header = Query(
            K15HidProtocol.LightingReadCommand,
            0,
            0,
            K15HidProtocol.LightingRecordSize);
        RequireLength(header, K15HidProtocol.LightingRecordSize, "lighting header");

        var baselineModeRepaired = false;

        // Owner baseline is explicit: Profile A = constant red, Profile B = constant blue.
        // A previous RGB canary can leave its persisted breathing/Tetris header behind if the
        // user switches away before the five-second overlay is restored. The underlying Constant
        // record is never modified by Status Lab, so changing only the header back to Constant
        // safely heals that stale notifier residue without inventing a new baseline color.
        if (slot <= 1 && header[0] is K15HidProtocol.SingleColorBreathingMode or K15HidProtocol.TetrisMode)
        {
            var repairedHeader = K15HidProtocol.CreateConstantHeader(header);
            WriteAndVerify(
                K15HidProtocol.LightingWriteCommand,
                K15HidProtocol.LightingReadCommand,
                0,
                0,
                repairedHeader,
                "repair stale notifier mode");
            header = repairedHeader;
            baselineModeRepaired = true;
            Thread.Sleep(20);
        }

        var breathingRecord = Query(
            K15HidProtocol.LightingReadCommand,
            0,
            K15HidProtocol.SingleColorBreathingAddress,
            K15HidProtocol.LightingRecordSize);

        RequireLength(breathingRecord, K15HidProtocol.LightingRecordSize, "breathing record");
        return new LightingSnapshot(slot, header, breathingRecord, baselineModeRepaired);
    }

    public void ApplyState(LightingSnapshot snapshot, K15NormalizedState state)
    {
        if (state == K15NormalizedState.Normal)
        {
            Restore(snapshot);
            return;
        }

        RequireSameActiveSlot(snapshot);

        if (state == K15NormalizedState.Running)
        {
            // RUNNING must be visually distinct from WAITING. Use the device's built-in
            // Tetris/Enraptured effect and leave its onboard Tetris detail record untouched.
            ApplyModeOnly(snapshot, K15HidProtocol.CreateRunningHeader(snapshot.Header), "running Tetris");
            return;
        }

        var speed = state switch
        {
            K15NormalizedState.Waiting => 6,
            K15NormalizedState.DonePendingAttention => 3,
            K15NormalizedState.Error => 6,
            _ => 3
        };

        var header = K15HidProtocol.CreateAlertHeader(snapshot.Header);
        var detail = K15HidProtocol.CreateAlertLightingRecord(
            state,
            brightness: state == K15NormalizedState.Error ? 6 : 5,
            speed: speed);

        ApplyBreathingRecord(snapshot, header, detail, "alert");
    }

    public void ApplyProfileFlash(LightingSnapshot snapshot)
    {
        RequireSameActiveSlot(snapshot);
        var header = K15HidProtocol.CreateAlertHeader(snapshot.Header);
        var detail = K15HidProtocol.CreateProfileFlashLightingRecord(snapshot.OnboardSlot);
        ApplyBreathingRecord(snapshot, header, detail, "profile flash");
    }

    private void ApplyModeOnly(
        LightingSnapshot snapshot,
        ReadOnlySpan<byte> header,
        string label)
    {
        RequireSameActiveSlot(snapshot);
        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            0,
            header,
            $"{label} lighting header");
    }

    private void ApplyBreathingRecord(
        LightingSnapshot snapshot,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> detail,
        string label)
    {
        // Write the hidden detail record first, then activate breathing. The previous order
        // activated an old breathing palette for a few milliseconds and produced visible
        // red/green flashes during state changes.
        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            K15HidProtocol.SingleColorBreathingAddress,
            detail,
            $"{label} breathing record");

        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            0,
            header,
            $"{label} lighting header");
    }

    public void Restore(LightingSnapshot snapshot)
    {
        RequireSameActiveSlot(snapshot);

        // Restore the baseline mode first. For the accepted A/B baseline this immediately
        // returns to Constant red/blue, making the subsequent breathing-record repair invisible.
        // The old reverse order caused a visible red breathing blip at NORMAL.
        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            0,
            snapshot.Header,
            "restore lighting header");

        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            K15HidProtocol.SingleColorBreathingAddress,
            snapshot.SingleColorBreathingRecord,
            "restore breathing record");
    }

    private void RequireSameActiveSlot(LightingSnapshot snapshot)
    {
        var current = ReadActiveSlot();
        if (current != snapshot.OnboardSlot)
            throw new K15ProfileChangedException(snapshot.OnboardSlot, current);
    }

    private void WriteAndVerify(
        byte writeCommand,
        byte readCommand,
        byte selector,
        ushort address,
        ReadOnlySpan<byte> data,
        string label)
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
        // Profile changes can briefly make the vendor collection return stale/no feature data.
        // Retry the complete request with a fresh sequence before surfacing a transport fault.
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
                    response[3] == command &&
                    response[4] == sequence)
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
        unchecked
        {
            _sequence++;
        }
        return _sequence;
    }

    private static bool MatchesVendorConfigurationCollection(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed) || preparsed == IntPtr.Zero)
            return false;

        try
        {
            var caps = new HidpCaps
            {
                Reserved = new ushort[17]
            };
            var status = HidP_GetCaps(preparsed, ref caps);
            return status >= 0 &&
                   caps.UsagePage == 0xFF01 &&
                   caps.Usage == 0x0001 &&
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

    internal sealed record LightingSnapshot(
        byte OnboardSlot,
        byte[] Header,
        byte[] SingleColorBreathingRecord,
        bool BaselineModeRepaired = false);

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

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

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

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
