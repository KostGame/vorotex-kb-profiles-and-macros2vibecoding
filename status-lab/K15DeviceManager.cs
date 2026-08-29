using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal enum K15DeviceConnectionState
{
    Disconnected,
    Scanning,
    Connected,
    ConnectionLost,
    Error
}

internal sealed record K15DeviceCandidate(
    string CandidateId,
    string Path,
    string ProductString,
    string SerialNumber,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportLength,
    bool? ProtocolVerified,
    string? VerificationResult)
{
    public string IdentityFingerprint => K15DeviceIdentity.CreateFingerprint(
        VendorId, ProductId, ProductString, SerialNumber, UsagePage, Usage, FeatureReportLength);
}

internal static class K15DeviceIdentity
{
    public static string CreateFingerprint(ushort vid, ushort pid, string product, string serial,
        ushort usagePage, ushort usage, ushort reportLength) =>
        $"{vid:X4}:{pid:X4}|{product.Trim()}|{serial.Trim()}|{usagePage:X4}:{usage:X4}|{reportLength}";

    public static K15DeviceCandidate? Resolve(IReadOnlyList<K15DeviceCandidate> candidates, string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return null;
        var matches = candidates.Where(c => c.IdentityFingerprint == preferred).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static string CandidateIdForPath(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}

internal sealed class K15DeviceManager : IDisposable
{
    private readonly string _preferencePath;
    private K15HidLightingController? _controller;
    private K15DeviceCandidate? _selected;
    private string? _preferredFingerprint;

    public K15DeviceManager(string preferencePath)
    {
        _preferencePath = preferencePath;
        LoadPreference();
    }

    public K15DeviceConnectionState ConnectionState { get; private set; } = K15DeviceConnectionState.Disconnected;
    public IReadOnlyList<K15DeviceCandidate> Candidates { get; private set; } = Array.Empty<K15DeviceCandidate>();
    public K15DeviceCandidate? SelectedDevice => _selected;
    public K15HidLightingController? Controller => _controller;
    public string? PreferredFingerprint => _preferredFingerprint;
    public event Action<K15DeviceConnectionState>? StateChanged;

    public IReadOnlyList<K15DeviceCandidate> GetCandidates() => Candidates;

    public IReadOnlyList<K15DeviceCandidate> Scan()
    {
        if (_controller is not null)
            throw new InvalidOperationException("Disconnect the current K15 device before scanning.");

        SetState(K15DeviceConnectionState.Scanning);
        try
        {
            Candidates = K15HidLightingController.ScanCandidates();
            _selected = null;
            SetState(K15DeviceConnectionState.Disconnected);
            Log("device_scan_completed", new { candidateCount = Candidates.Count });
            return Candidates;
        }
        catch (Exception ex)
        {
            SetState(K15DeviceConnectionState.Error);
            Log("device_scan_failed", new { exception = ex.GetType().Name, hresult = ex.HResult });
            throw;
        }
    }

    public bool TryResolvePreferred()
    {
        var candidate = K15DeviceIdentity.Resolve(Candidates, _preferredFingerprint);
        if (candidate is null && !string.IsNullOrWhiteSpace(_preferredFingerprint))
            Log("device_preferred_identity_unresolved", new { ambiguousOrMissing = true });
        return Select(candidate);
    }

    public bool SelectById(string candidateId)
    {
        var candidate = Candidates.SingleOrDefault(c => c.CandidateId == candidateId);
        return Select(candidate);
    }

    public bool Select(K15DeviceCandidate? candidate)
    {
        if (candidate is null || !Candidates.Any(c => c.CandidateId == candidate.CandidateId)) return false;
        _selected = candidate;
        Log("device_selected", new { identity = candidate.IdentityFingerprint, candidateId = candidate.CandidateId });
        return true;
    }

    public bool Connect()
    {
        if (_selected is null) return false;
        Disconnect();
        K15HidLightingController? pendingController = null;
        try
        {
            pendingController = K15HidLightingController.Open(_selected.Path);
            var slot = pendingController.ReadActiveSlot();
            if (slot > 1) throw new InvalidDataException("K15 protocol verification returned an invalid slot.");
            _selected = _selected with { ProtocolVerified = true, VerificationResult = "0x82 PASS" };
            Candidates = Candidates.Select(c => c.CandidateId == _selected.CandidateId ? _selected : c).ToArray();
            _controller = pendingController;
            pendingController = null;
            _preferredFingerprint = _selected.IdentityFingerprint;
            SavePreference();
            SetState(K15DeviceConnectionState.Connected);
            Log("device_connected", new { identity = _selected.IdentityFingerprint, protocolVerification = "PASS" });
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            pendingController?.Dispose();
            _controller?.Dispose();
            _controller = null;
            _selected = _selected with { ProtocolVerified = false, VerificationResult = VerificationResultFor(ex) };
            Candidates = Candidates.Select(c => c.CandidateId == _selected.CandidateId ? _selected : c).ToArray();
            SetState(K15DeviceConnectionState.Error);
            Log("device_protocol_verification_failed", new { identity = _selected.IdentityFingerprint, result = _selected.VerificationResult });
            return false;
        }
    }

    public bool Connect(K15DeviceCandidate candidate) => Select(candidate) && Connect();

    public bool Reconnect()
    {
        if (_selected is null) return false;
        return Connect();
    }

    public void MarkConnectionLost()
    {
        _controller?.Dispose();
        _controller = null;
        SetState(K15DeviceConnectionState.ConnectionLost);
        Log("device_connection_lost", new { identity = _selected?.IdentityFingerprint });
    }

    public void Disconnect()
    {
        _controller?.Dispose();
        _controller = null;
        SetState(K15DeviceConnectionState.Disconnected);
        Log("device_disconnected", new { identity = _selected?.IdentityFingerprint });
    }

    private static string VerificationResultFor(Exception ex) => ex switch
    {
        TimeoutException => "0x82 timeout",
        InvalidDataException => "0x82 invalid response",
        _ => "open/verify failed"
    };

    private void SetState(K15DeviceConnectionState state)
    {
        ConnectionState = state;
        StateChanged?.Invoke(state);
    }

    private void LoadPreference()
    {
        try
        {
            if (File.Exists(_preferencePath))
                _preferredFingerprint = JsonSerializer.Deserialize<Preference>(File.ReadAllText(_preferencePath))?.Fingerprint;
        }
        catch { _preferredFingerprint = null; }
    }

    private void SavePreference()
    {
        var directory = Path.GetDirectoryName(_preferencePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_preferencePath, JsonSerializer.Serialize(new Preference(_preferredFingerprint)));
    }

    private sealed record Preference(string? Fingerprint);
    private static void Log(string name, object details) => EventJournal.Append(new
    {
        timestampUtc = DateTimeOffset.UtcNow,
        source = "k15_device_manager",
        @event = name,
        details
    });

    public void Dispose() => Disconnect();
}
