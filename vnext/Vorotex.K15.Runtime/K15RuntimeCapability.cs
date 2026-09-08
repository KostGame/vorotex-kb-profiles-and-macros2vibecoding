using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

internal sealed record RuntimeDeviceCandidate(string CandidateId, string Path, string Product, string Serial,
    ushort VendorId, ushort ProductId, ushort UsagePage = 0, ushort Usage = 0, ushort FeatureReportLength = K15HidProtocol.ReportSize,
    bool? ProtocolVerified = null, string? Verification = null)
{
    public string Identity => $"{VendorId:X4}:{ProductId:X4}|{Product.Trim()}|{Serial.Trim()}|{UsagePage:X4}:{Usage:X4}|{FeatureReportLength}";
    public static string IdForPath(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)).AsSpan(0, 8));
}

internal interface IK15DeviceHandle : IDisposable
{
    string CandidateId { get; }
    byte ActiveSlot { get; }
    void VerifyProtocol();
    RuntimeLightingSnapshot CaptureLightingSnapshot();
    void ApplyEffect(RuntimeLightingEffect effect);
    void RestoreLighting(RuntimeLightingSnapshot snapshot);
}

internal sealed record RuntimeLightingSnapshot(byte ActiveSlot, byte[] Header, IReadOnlyDictionary<byte, byte[]> ModeRecords);

internal enum RuntimeWireColorOrder { Rgb, Grb }

internal static class RuntimeRgbPolicy
{
    public static RuntimeLightingEffect? ForState(RuntimeState state, byte activeSlot,
        string profileA = "#FF0000", string profileB = "#0000FF", RuntimeWireColorOrder order = RuntimeWireColorOrder.Rgb)
    {
        var color = ParseColor(activeSlot == 0 ? profileA : profileB);
        var palette = ImmutableArray.Create(color);
        return state switch
        {
            RuntimeState.Running => new(K15HidProtocol.FlowingWaterMode, 4, 3, 0, palette),
            RuntimeState.Waiting => new(K15HidProtocol.SingleColorBreathingMode, 6, 7, 0, palette),
            RuntimeState.DonePendingAttention => new(K15HidProtocol.SingleColorBreathingMode, 6, 5, 0, palette),
            RuntimeState.Blocked => null,
            _ => new(K15HidProtocol.ConstantMode, 6, 4, 0, palette, 0x01)
        };
    }

    public static RuntimeRgbColor ToWire(RuntimeRgbColor color, RuntimeWireColorOrder order) =>
        order == RuntimeWireColorOrder.Rgb ? color : new(color.G, color.R, color.B);

    private static RuntimeRgbColor ParseColor(string value)
    {
        if (value.Length != 7 || value[0] != '#') throw new InvalidDataException("RGB profile color must be #RRGGBB.");
        return new(Convert.ToByte(value[1..3], 16), Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16));
    }
}

internal interface IK15DeviceBackend
{
    IReadOnlyList<RuntimeDeviceCandidate> Scan();
    IK15DeviceHandle Open(RuntimeDeviceCandidate candidate);
}

/// <summary>Safe default: the runtime never opens a physical HID endpoint during bootstrap/tests.</summary>
internal sealed class DisabledHidBackend : IK15DeviceBackend
{
    public IReadOnlyList<RuntimeDeviceCandidate> Scan() => Array.Empty<RuntimeDeviceCandidate>();
    public IK15DeviceHandle Open(RuntimeDeviceCandidate candidate) => throw new InvalidOperationException("PHYSICAL_HID_BACKEND_NOT_ENABLED");
}

internal sealed class RuntimeDeviceManager : IDisposable
{
    private const int MaxCandidates = 32;
    private const int MaxText = 64;
    private readonly object _gate = new();
    private readonly IK15DeviceBackend _backend;
    private RuntimeDeviceCandidate? _selected;
    private IK15DeviceHandle? _handle;
    private string? _preferredIdentity;
    private string? _lastFailure;
    private List<RuntimeDeviceCandidate> _candidates = new();
    public event Action? OwnershipLost;

    public RuntimeDeviceManager(IK15DeviceBackend? backend = null) => _backend = backend ?? new DisabledHidBackend();
    public string ConnectionState { get; private set; } = "DISCONNECTED";
    public IReadOnlyList<RuntimeDeviceCandidate> Candidates { get { lock (_gate) return _candidates.ToArray(); } }
    public RuntimeDeviceCandidate? Selected { get { lock (_gate) return _selected; } }
    public bool IsConnected { get { lock (_gate) return _handle is not null; } }
    public string? LastFailure { get { lock (_gate) return _lastFailure; } }
    public IK15DeviceHandle? Handle { get { lock (_gate) return _handle; } }
    internal RuntimeDeviceView View { get { lock (_gate) return new RuntimeDeviceView(_selected?.CandidateId, _handle, _handle is not null); } }

    public IReadOnlyList<RuntimeDeviceCandidate> Scan()
    {
        IReadOnlyList<RuntimeDeviceCandidate> result;
        var lost = false;
        lock (_gate)
        {
            if (_handle is not null) { _lastFailure = "DISCONNECT_BEFORE_SCAN"; return _candidates.ToArray(); }
            try
            {
                ConnectionState = "SCANNING";
                _candidates = _backend.Scan().Take(MaxCandidates).Select(Bound).ToList();
                lost = _selected is not null;
                _selected = null;
                _lastFailure = null;
                ConnectionState = "DISCONNECTED";
            }
            catch { ConnectionState = "ERROR"; _lastFailure = "SCAN_FAILED"; }
            result = _candidates.ToArray();
        }
        if (lost) OwnershipLost?.Invoke();
        return result;
    }

    public bool Select(string candidateId)
    {
        var lost = false;
        var selected = false;
        lock (_gate)
        {
            var candidate = _candidates.SingleOrDefault(x => x.CandidateId == candidateId);
            if (candidate is null) { _lastFailure = "CANDIDATE_NOT_FOUND"; return false; }
            lost = _selected?.CandidateId != candidate.CandidateId;
            _selected = candidate; _lastFailure = null; selected = true;
        }
        if (lost) OwnershipLost?.Invoke();
        return selected;
    }

    public bool SelectPreferred(string? preferredIdentity = null)
    {
        RuntimeDeviceCandidate[] matches;
        lock (_gate)
        {
            if (preferredIdentity is not null) _preferredIdentity = preferredIdentity;
            matches = string.IsNullOrWhiteSpace(_preferredIdentity) ? Array.Empty<RuntimeDeviceCandidate>() :
                _candidates.Where(x => x.Identity == _preferredIdentity).ToArray();
        }
        return matches.Length == 1 && Select(matches[0].CandidateId);
    }

    public bool Connect()
    {
        var lost = false;
        var connected = false;
        lock (_gate)
        {
            if (_selected is null) { _lastFailure = "CANDIDATE_NOT_SELECTED"; return false; }
            var replaced = _handle is not null;
            _handle?.Dispose(); _handle = null;
            lost = replaced;
            try
            {
                var pending = _backend.Open(_selected);
                try { pending.VerifyProtocol(); }
                catch { pending.Dispose(); MarkVerificationFailure(); }
                if (_lastFailure is null)
                {
                    _handle = pending;
                    _selected = _selected with { ProtocolVerified = true, Verification = "PASS" };
                    _candidates = _candidates.Select(x => x.CandidateId == _selected.CandidateId ? _selected : x).ToList();
                    _preferredIdentity = _selected.Identity;
                    ConnectionState = "CONNECTED"; _lastFailure = null;
                    connected = true;
                }
            }
            catch { ConnectionState = "ERROR"; _lastFailure = "OPEN_OR_VERIFY_FAILED"; }
        }
        if (lost) OwnershipLost?.Invoke();
        return connected;
    }

    public bool Connect(string candidateId) => Select(candidateId) && Connect();
    public bool Reconnect() { lock (_gate) { if (_selected is null) return false; } return Connect(); }
    public void Disconnect(string state = "DISCONNECTED") { var lost = false; lock (_gate) { lost = _handle is not null || _selected is not null; _handle?.Dispose(); _handle = null; ConnectionState = state; } if (lost) OwnershipLost?.Invoke(); }
    public void MarkConnectionLost() => Disconnect("CONNECTION_LOST");
    private void MarkVerificationFailure() { ConnectionState = "ERROR"; _lastFailure = "PROTOCOL_VERIFY_FAILED"; _selected = _selected is null ? null : _selected with { ProtocolVerified = false, Verification = "FAIL" }; }
    private static RuntimeDeviceCandidate Bound(RuntimeDeviceCandidate c) => c with { CandidateId = Limit(c.CandidateId), Product = Limit(c.Product), Serial = Limit(c.Serial) };
    private static string Limit(string value) => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, MaxText)];
    public void Dispose() => Disconnect();
}

internal sealed record RuntimeDeviceView(string? SelectedCandidateId, IK15DeviceHandle? Handle, bool Connected);

internal sealed class RuntimeRgbController
{
    private readonly object _gate = new();
    private readonly RuntimeDeviceManager _devices;
    private RuntimeLightingSnapshot? _restore;
    private string? _restoreCandidate;
    private string? _lastFailure;
    public RuntimeRgbController(RuntimeDeviceManager devices) { _devices = devices; _devices.OwnershipLost += InvalidateRestoreSnapshot; }
    public bool Enabled { get; private set; }
    public string Effect { get; private set; } = "NORMAL";
    public string? LastFailure { get { lock (_gate) return _lastFailure; } }
    public bool RestoreAvailable { get { lock (_gate) { var view = _devices.View; return _restore is not null && _restoreCandidate == view.SelectedCandidateId && view.Connected; } } }
    public bool TransportAvailable => _devices.View.Connected;
    internal void Disarm() => InvalidateRestoreSnapshot();

    public bool SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (!enabled) { Enabled = false; Effect = "OFF"; _lastFailure = null; return true; }
            if (Enabled) return true;
            var view = _devices.View;
            if (!view.Connected || view.Handle is null) { _lastFailure = "DEVICE_UNAVAILABLE"; return false; }
            try { _restore = view.Handle.CaptureLightingSnapshot(); _restoreCandidate = view.SelectedCandidateId; Enabled = true; _lastFailure = null; return true; }
            catch { _lastFailure = "SNAPSHOT_FAILED"; return false; }
        }
    }

    private void InvalidateRestoreSnapshot() { lock (_gate) { _restore = null; _restoreCandidate = null; Enabled = false; Effect = "OFF"; } }

    public void ApplyRuntimeState(RuntimeState state)
    {
        var connectionLost = false;
        lock (_gate)
        {
            Effect = state switch { RuntimeState.Waiting => "WAITING", RuntimeState.Blocked => "BLOCKED", RuntimeState.DonePendingAttention => "ATTENTION", RuntimeState.Running => "RUNNING", _ => "NORMAL" };
            var view = _devices.View;
            if (!Enabled || !view.Connected || view.Handle is null) return;
            try { var policy = RuntimeRgbPolicy.ForState(state, view.Handle.ActiveSlot); if (policy is not null) view.Handle.ApplyEffect(policy); _lastFailure = null; } catch { _lastFailure = "EFFECT_WRITE_FAILED"; connectionLost = true; }
        }
        if (connectionLost) _devices.MarkConnectionLost();
    }

    public bool RestoreLighting()
    {
        lock (_gate)
        {
            var view = _devices.View;
            if (_restore is null || _restoreCandidate != view.SelectedCandidateId || !view.Connected || view.Handle is null) { _lastFailure = "RESTORE_SNAPSHOT_UNAVAILABLE"; return false; }
            try { view.Handle.RestoreLighting(_restore); _lastFailure = null; return true; } catch { _lastFailure = "RESTORE_FAILED"; return false; }
        }
    }
}
