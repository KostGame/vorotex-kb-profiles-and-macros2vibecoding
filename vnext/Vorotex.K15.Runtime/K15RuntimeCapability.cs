using System.Security.Cryptography;
using System.Text;
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
    void VerifyProtocol();
    byte[] CaptureLightingSnapshot();
    void ApplyEffect(string effect);
    void RestoreLighting(byte[] snapshot);
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

    public IReadOnlyList<RuntimeDeviceCandidate> Scan()
    {
        lock (_gate)
        {
            if (_handle is not null) { _lastFailure = "DISCONNECT_BEFORE_SCAN"; return _candidates.ToArray(); }
            try
            {
                ConnectionState = "SCANNING";
                _candidates = _backend.Scan().Take(MaxCandidates).Select(Bound).ToList();
                _selected = null;
                _lastFailure = null;
                ConnectionState = "DISCONNECTED";
            }
            catch { ConnectionState = "ERROR"; _lastFailure = "SCAN_FAILED"; }
            return _candidates.ToArray();
        }
    }

    public bool Select(string candidateId)
    {
        lock (_gate)
        {
            var candidate = _candidates.SingleOrDefault(x => x.CandidateId == candidateId);
            if (candidate is null) { _lastFailure = "CANDIDATE_NOT_FOUND"; return false; }
            if (_selected?.CandidateId != candidate.CandidateId) OwnershipLost?.Invoke();
            _selected = candidate; _lastFailure = null; return true;
        }
    }

    public bool SelectPreferred(string? preferredIdentity = null)
    {
        lock (_gate)
        {
            if (preferredIdentity is not null) _preferredIdentity = preferredIdentity;
            var matches = string.IsNullOrWhiteSpace(_preferredIdentity) ? Array.Empty<RuntimeDeviceCandidate>() :
                _candidates.Where(x => x.Identity == _preferredIdentity).ToArray();
            return matches.Length == 1 && Select(matches[0].CandidateId);
        }
    }

    public bool Connect()
    {
        lock (_gate)
        {
            if (_selected is null) { _lastFailure = "CANDIDATE_NOT_SELECTED"; return false; }
            var replaced = _handle is not null;
            _handle?.Dispose(); _handle = null;
            if (replaced) OwnershipLost?.Invoke();
            try
            {
                var pending = _backend.Open(_selected);
                try { pending.VerifyProtocol(); }
                catch { pending.Dispose(); MarkVerificationFailure(); return false; }
                _handle = pending;
                _selected = _selected with { ProtocolVerified = true, Verification = "PASS" };
                _candidates = _candidates.Select(x => x.CandidateId == _selected.CandidateId ? _selected : x).ToList();
                _preferredIdentity = _selected.Identity;
                ConnectionState = "CONNECTED"; _lastFailure = null; return true;
            }
            catch { ConnectionState = "ERROR"; _lastFailure = "OPEN_OR_VERIFY_FAILED"; return false; }
        }
    }

    public bool Connect(string candidateId) => Select(candidateId) && Connect();
    public bool Reconnect() { lock (_gate) { return _selected is not null && Connect(); } }
    public void Disconnect(string state = "DISCONNECTED") { lock (_gate) { _handle?.Dispose(); _handle = null; OwnershipLost?.Invoke(); ConnectionState = state; } }
    public void MarkConnectionLost() => Disconnect("CONNECTION_LOST");
    private void MarkVerificationFailure() { ConnectionState = "ERROR"; _lastFailure = "PROTOCOL_VERIFY_FAILED"; _selected = _selected is null ? null : _selected with { ProtocolVerified = false, Verification = "FAIL" }; }
    private static RuntimeDeviceCandidate Bound(RuntimeDeviceCandidate c) => c with { CandidateId = Limit(c.CandidateId), Product = Limit(c.Product), Serial = Limit(c.Serial) };
    private static string Limit(string value) => (value ?? string.Empty).Trim()[..Math.Min((value ?? string.Empty).Trim().Length, MaxText)];
    public void Dispose() => Disconnect();
}

internal sealed class RuntimeRgbController
{
    private readonly object _gate = new();
    private readonly RuntimeDeviceManager _devices;
    private byte[]? _restore;
    private string? _restoreCandidate;
    private string? _lastFailure;
    public RuntimeRgbController(RuntimeDeviceManager devices) { _devices = devices; _devices.OwnershipLost += InvalidateRestoreSnapshot; }
    public bool Enabled { get; private set; }
    public string Effect { get; private set; } = "NORMAL";
    public string? LastFailure { get { lock (_gate) return _lastFailure; } }
    public bool RestoreAvailable { get { lock (_gate) return _restore is not null && _restoreCandidate == _devices.Selected?.CandidateId && _devices.IsConnected; } }
    public bool TransportAvailable => _devices.IsConnected;

    public bool SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (!enabled) { Enabled = false; Effect = "OFF"; _lastFailure = null; return true; }
            if (Enabled) return true;
            if (!_devices.IsConnected || _devices.Handle is null) { _lastFailure = "DEVICE_UNAVAILABLE"; return false; }
            try { _restore = _devices.Handle.CaptureLightingSnapshot(); _restoreCandidate = _devices.Selected!.CandidateId; Enabled = true; _lastFailure = null; return true; }
            catch { _lastFailure = "SNAPSHOT_FAILED"; return false; }
        }
    }

    private void InvalidateRestoreSnapshot() { lock (_gate) { _restore = null; _restoreCandidate = null; } }

    public void ApplyRuntimeState(RuntimeState state)
    {
        lock (_gate)
        {
            Effect = state switch { RuntimeState.Waiting => "WAITING", RuntimeState.Blocked => "BLOCKED", RuntimeState.DonePendingAttention => "ATTENTION", RuntimeState.Running => "RUNNING", _ => "NORMAL" };
            if (!Enabled || !_devices.IsConnected || _devices.Handle is null) return;
            try { _devices.Handle.ApplyEffect(Effect); _lastFailure = null; } catch { _lastFailure = "EFFECT_WRITE_FAILED"; _devices.MarkConnectionLost(); }
        }
    }

    public bool RestoreLighting()
    {
        lock (_gate)
        {
            if (!RestoreAvailable) { _lastFailure = "RESTORE_SNAPSHOT_UNAVAILABLE"; return false; }
            try { _devices.Handle!.RestoreLighting(_restore!); _lastFailure = null; return true; } catch { _lastFailure = "RESTORE_FAILED"; return false; }
        }
    }
}
