using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vorotex.K15.Runtime;

/// <summary>
/// Production-capable Windows K15 HID backend. Construction is explicit; RuntimeHost uses
/// DisabledHidBackend by default, so this code cannot activate or touch an owner device at startup.
/// </summary>
internal sealed class WindowsK15HidBackend : IK15DeviceBackend
{
    private readonly RuntimeWireColorOrder _wireOrder;
    public WindowsK15HidBackend(RuntimeWireColorOrder wireOrder = RuntimeWireColorOrder.Rgb) => _wireOrder = wireOrder;
    public IReadOnlyList<RuntimeDeviceCandidate> Scan()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<RuntimeDeviceCandidate>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new List<RuntimeDeviceCandidate>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                    continue;
                }
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, required, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    using var handle = OpenHandle(path);
                    if (handle.IsInvalid || !TryDescribe(handle, out var description)) continue;
                    result.Add(new RuntimeDeviceCandidate(RuntimeDeviceCandidate.IdForPath(path), path,
                        description.Product, description.Serial, description.VendorId, description.ProductId,
                        description.UsagePage, description.Usage, description.FeatureReportLength));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public IK15DeviceHandle Open(RuntimeDeviceCandidate candidate)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("K15 HID requires Windows.");
        if (string.IsNullOrWhiteSpace(candidate.Path)) throw new ArgumentException("Selected endpoint is required.");
        var handle = OpenHandle(candidate.Path);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!TryDescribe(handle, out var description) || description.VendorId != candidate.VendorId || description.ProductId != candidate.ProductId ||
            description.UsagePage != candidate.UsagePage || description.Usage != candidate.Usage || description.FeatureReportLength != candidate.FeatureReportLength)
        { handle.Dispose(); throw new InvalidDataException("Selected endpoint identity or K15 collection no longer matches."); }
        if (!string.Equals(description.Product, candidate.Product, StringComparison.Ordinal) || !string.Equals(description.Serial, candidate.Serial, StringComparison.Ordinal))
        { handle.Dispose(); throw new InvalidDataException("Selected endpoint product or serial changed."); }
        return new WindowsK15DeviceHandle(candidate.CandidateId, handle, _wireOrder);
    }

    private static SafeFileHandle OpenHandle(string path) => CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
        IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    private static bool TryDescribe(SafeFileHandle handle, out HidDescription description)
    {
        description = default;
        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || !K15HidProtocol.IsSupportedDevice(attributes.VendorID, attributes.ProductID)) return false;
        if (!TryGetCaps(handle, out var caps) || caps.UsagePage != 0xFF01 || caps.Usage != 0x0001 || caps.FeatureReportByteLength != K15HidProtocol.ReportSize) return false;
        description = new HidDescription(attributes.VendorID, attributes.ProductID, caps.UsagePage, caps.Usage,
            caps.FeatureReportByteLength, ReadHidString(handle, true), ReadHidString(handle, false));
        return true;
    }

    private static string ReadHidString(SafeFileHandle handle, bool product)
    {
        var buffer = new byte[512];
        var ok = product ? HidD_GetProductString(handle, buffer, buffer.Length) : HidD_GetSerialNumberString(handle, buffer, buffer.Length);
        return ok ? Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim() : string.Empty;
    }

    private static bool TryGetCaps(SafeFileHandle handle, out HidpCaps caps)
    {
        caps = new HidpCaps { Reserved = new ushort[17] };
        if (!HidD_GetPreparsedData(handle, out var preparsed) || preparsed == IntPtr.Zero) return false;
        try { return HidP_GetCaps(preparsed, ref caps) >= 0; }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private readonly record struct HidDescription(ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage,
        ushort FeatureReportLength, string Product, string Serial);
    private const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10, GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3, ErrorNoMoreItems = 259;

    [StructLayout(LayoutKind.Sequential)] private struct SpDeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct HiddAttributes { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
            NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, ref HidpCaps caps);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, uint index, ref SpDeviceInterfaceData iface);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData iface, IntPtr detail, uint size, out uint required, IntPtr data);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}

internal sealed class WindowsK15DeviceHandle : IK15DeviceHandle
{
    private readonly SafeFileHandle _handle;
    private readonly RuntimeWireColorOrder _wireOrder;
    private byte _sequence;
    private byte _activeSlot;
    public WindowsK15DeviceHandle(string candidateId, SafeFileHandle handle, RuntimeWireColorOrder wireOrder) { CandidateId = candidateId; _handle = handle; _wireOrder = wireOrder; }
    public string CandidateId { get; }
    public byte ActiveSlot => _activeSlot;
    public byte ReadActiveSlot() { _activeSlot = ReadActiveSlotCore(); return _activeSlot; }
    public void VerifyProtocol() { _activeSlot = ReadActiveSlotCore(); }

    public RuntimeLightingSnapshot CaptureLightingSnapshot()
    {
        var slot = ReadActiveSlot();
        var header = Query(K15HidProtocol.LightingReadCommand, 0, 0, K15HidProtocol.LightingRecordSize);
        K15HidProtocol.ValidateReply(header, K15HidProtocol.LightingRecordSize);
        var records = new Dictionary<byte, byte[]>();
        foreach (var mode in ProvenModes(header[0]))
        {
            var record = Query(K15HidProtocol.LightingReadCommand, 0, K15HidProtocol.ModeRecordAddress(mode), K15HidProtocol.LightingRecordSize);
            K15HidProtocol.ValidateReply(record, K15HidProtocol.LightingRecordSize);
            records[mode] = record;
        }
        return new RuntimeLightingSnapshot(slot, header, records);
    }

    public void ApplyEffect(RuntimeLightingEffect effect)
    {
        if (ReadActiveSlot() != effect.TargetSlot) throw new InvalidDataException("K15 active profile changed before effect write.");
        var currentHeader = Query(K15HidProtocol.LightingReadCommand, 0, 0, K15HidProtocol.LightingRecordSize);
        var header = K15HidProtocol.CreateModeHeader(currentHeader, effect.Mode);
        if (effect.Mode != K15HidProtocol.OffMode)
            WriteAndVerify(K15HidProtocol.LightingWriteCommand, K15HidProtocol.LightingReadCommand, 0, K15HidProtocol.ModeRecordAddress(effect.Mode), K15HidProtocol.CreateEffectRecord(effect, _wireOrder));
        WriteAndVerify(K15HidProtocol.LightingWriteCommand, K15HidProtocol.LightingReadCommand, 0, 0, header);
    }

    public void RestoreLighting(RuntimeLightingSnapshot snapshot)
    {
        K15HidProtocol.ValidateReply(snapshot.Header, K15HidProtocol.LightingRecordSize);
        if (snapshot.ActiveSlot > 1 || snapshot.ModeRecords.Any(pair => pair.Key == K15HidProtocol.OffMode || pair.Value.Length != K15HidProtocol.LightingRecordSize))
            throw new InvalidDataException("K15 lighting snapshot contains an invalid mode record.");
        if (ReadActiveSlot() != snapshot.ActiveSlot) throw new InvalidDataException("K15 active profile changed.");
        foreach (var write in K15HidProtocol.RestorePlan(snapshot))
            WriteAndVerify(K15HidProtocol.LightingWriteCommand, K15HidProtocol.LightingReadCommand, 0, write.Address, write.Data);
    }

    private byte ReadActiveSlotCore()
    {
        for (var attempt = 0; attempt < 6; attempt++) { var value = Query(K15HidProtocol.DeviceReadCommand, K15HidProtocol.ActiveSlotSelector, 0, 1); K15HidProtocol.ValidateReply(value, 1); if (value[0] <= 1) return value[0]; Thread.Sleep(60); }
        throw new TimeoutException("K15 active slot did not stabilize.");
    }
    private void WriteAndVerify(byte write, byte read, byte selector, ushort address, ReadOnlySpan<byte> data) { Write(write, selector, address, data); if (!Query(read, selector, address, (byte)data.Length).AsSpan().SequenceEqual(data)) throw new InvalidDataException("K15 readback mismatch."); }
    private void Write(byte command, byte selector, ushort address, ReadOnlySpan<byte> data) { var report = K15HidProtocol.FrameReport(command, ++_sequence, selector, address, data); if (!HidD_SetFeature(_handle, report, report.Length)) throw new Win32Exception(Marshal.GetLastWin32Error()); Thread.Sleep(8); }
    private byte[] Query(byte command, byte selector, ushort address, byte length)
    {
        for (var requestAttempt = 0; requestAttempt < 3; requestAttempt++)
        {
            var sequence = ++_sequence; var request = K15HidProtocol.ReadRequest(command, sequence, selector, address, length);
            if (!HidD_SetFeature(_handle, request, request.Length)) { Thread.Sleep(35); continue; }
            for (var poll = 0; poll < 6; poll++) { Thread.Sleep(20); var response = new byte[K15HidProtocol.ReportSize]; response[0] = K15HidProtocol.ReportId; if (HidD_GetFeature(_handle, response, response.Length) && response[3] == command && response[4] == sequence && response[8] <= K15HidProtocol.MaxData) return response.AsSpan(9, response[8]).ToArray(); }
        }
        throw new TimeoutException("No matching K15 HID response.");
    }
    private static IEnumerable<byte> ProvenModes(byte baseline) => new[] { K15HidProtocol.ConstantMode, K15HidProtocol.FlowingWaterMode, K15HidProtocol.SingleColorBreathingMode, K15HidProtocol.CycleBreathingMode }.Concat(baseline is K15HidProtocol.OffMode ? Array.Empty<byte>() : new[] { baseline }).Distinct();
    public void Dispose() => _handle.Dispose();
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] report, int length);
}
