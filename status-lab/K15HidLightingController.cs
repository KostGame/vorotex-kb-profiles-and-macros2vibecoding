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
        var data = Query(K15HidProtocol.DeviceReadCommand, K15HidProtocol.ActiveSlotSelector, 0, 1);
        if (data.Length != 1 || data[0] > 1)
            throw new InvalidDataException("K15 returned an invalid active onboard slot.");
        return data[0];
    }

    public LightingSnapshot CaptureLightingSnapshot()
    {
        var slot = ReadActiveSlot();
        var header = Query(
            K15HidProtocol.LightingReadCommand,
            0,
            0,
            K15HidProtocol.LightingRecordSize);
        var breathingRecord = Query(
            K15HidProtocol.LightingReadCommand,
            0,
            K15HidProtocol.SingleColorBreathingAddress,
            K15HidProtocol.LightingRecordSize);

        RequireLength(header, K15HidProtocol.LightingRecordSize, "lighting header");
        RequireLength(breathingRecord, K15HidProtocol.LightingRecordSize, "breathing record");
        return new LightingSnapshot(slot, header, breathingRecord);
    }

    public void ApplyState(LightingSnapshot snapshot, K15NormalizedState state)
    {
        if (state == K15NormalizedState.Normal)
        {
            Restore(snapshot);
            return;
        }

        RequireSameActiveSlot(snapshot);

        var speed = state switch
        {
            K15NormalizedState.Running => 2,
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

    private void ApplyBreathingRecord(
        LightingSnapshot snapshot,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> detail,
        string label)
    {
        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            0,
            header,
            $"{label} lighting header");

        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            K15HidProtocol.SingleColorBreathingAddress,
            detail,
            $"{label} breathing record");
    }

    public void Restore(LightingSnapshot snapshot)
    {
        RequireSameActiveSlot(snapshot);

        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            K15HidProtocol.SingleColorBreathingAddress,
            snapshot.SingleColorBreathingRecord,
            "restore breathing record");

        WriteAndVerify(
            K15HidProtocol.LightingWriteCommand,
            K15HidProtocol.LightingReadCommand,
            0,
            0,
            snapshot.Header,
            "restore lighting header");
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
        byte[] SingleColorBreathingRecord);

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
