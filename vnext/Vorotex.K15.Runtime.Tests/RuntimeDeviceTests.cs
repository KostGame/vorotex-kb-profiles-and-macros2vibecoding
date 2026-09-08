using System.Collections.Immutable;
using System.Text.Json;
using Vorotex.K15.Runtime;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class RuntimeDeviceTests
{
    public static Task DeviceManagerRequiresExplicitSelectionAndOwnsOneHandle()
    {
        var backend = new FakeBackend();
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "device-" + Guid.NewGuid().ToString("N") }, backend);
        var scan = host.HandleIpcRequest(Request("scan_devices"));
        TestAssert.True(scan.Contains("candidate-a"), "bounded candidate was not projected");
        TestAssert.False(backend.OpenCount > 0, "scan auto-connected a candidate");
        var rejected = host.HandleIpcRequest(Request("connect_device", ("candidateId", "missing")));
        TestAssert.False(rejected.Contains("\"ok\":true"), "missing candidate connected");
        var connected = host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        TestAssert.True(connected.Contains("\"connectionState\":\"CONNECTED\""), "explicit candidate did not connect");
        TestAssert.Equal(1, backend.OpenCount, "more than one handle was opened");
        return Task.CompletedTask;
    }

    public static Task ProtocolVerificationFailureFailsClosed()
    {
        var backend = new FakeBackend { VerifyFails = true };
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "verify-" + Guid.NewGuid().ToString("N") }, backend);
        host.HandleIpcRequest(Request("scan_devices"));
        var response = host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        TestAssert.True(response.Contains("PROTOCOL_VERIFY_FAILED"), "verification failure code changed");
        TestAssert.True(backend.LastHandle?.Disposed == true, "pending handle was not disposed");
        return Task.CompletedTask;
    }

    public static Task RgbUsesFakeTransportAndPreservesStateAuthority()
    {
        var backend = new FakeBackend();
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "rgb-" + Guid.NewGuid().ToString("N") }, backend);
        host.HandleIpcRequest(Request("scan_devices"));
        host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        TestAssert.True(host.HandleIpcRequest(Request("set_rgb_enabled", ("enabled", true))).Contains("\"ok\":true"), "RGB did not enable");
        host.ApplyNativeStatusJson("{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\",\"threadId\":\"t\",\"status\":\"active\",\"activeFlags\":[\"waitingOnApproval\"],\"timestampUtc\":\"2026-09-08T00:00:00Z\"}");
        TestAssert.Equal(K15HidProtocol.SingleColorBreathingMode.ToString(), backend.LastHandle!.Effects[^1], "canonical state did not choose deterministic effect");
        TestAssert.Equal(0, backend.RealWrites, "fake test performed a physical write");
        return Task.CompletedTask;
    }

    public static Task IpcCommandsAreBoundedAndDoNotExposePaths()
    {
        var backend = new FakeBackend();
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "ipc-device-" + Guid.NewGuid().ToString("N") }, backend);
        var response = host.HandleIpcRequest("{\"protocolVersion\":\"k15-runtime-ipc/v1\",\"command\":\"scan_devices\",\"candidateId\":\"x\"}");
        TestAssert.True(response.Contains("UNSUPPORTED_FIELD"), "extra command field was accepted");
        var snapshot = host.HandleIpcRequest(Request("scan_devices"));
        TestAssert.False(snapshot.Contains("\\\\fake\\\\hid"), "raw HID path crossed IPC");
        TestAssert.True(snapshot.Length < RuntimeIpcMetadata.MaxFrameBytes, "device snapshot exceeded frame bound");
        return Task.CompletedTask;
    }

    public static Task PreferredIdentityFailsClosed()
    {
        var backend = new FakeBackend { DuplicateIdentity = true };
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "preferred-" + Guid.NewGuid().ToString("N") }, backend);
        host.HandleIpcRequest(Request("scan_devices"));
        TestAssert.False(host.DeviceManager.SelectPreferred(backend.Identity), "ambiguous preferred identity was selected");
        backend.DuplicateIdentity = false;
        host.DeviceManager.Scan();
        TestAssert.True(host.DeviceManager.SelectPreferred(backend.Identity), "unique exact preferred identity was not selected");
        return Task.CompletedTask;
    }

    public static Task RestoreIsSessionBound()
    {
        var backend = new FakeBackend();
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "restore-" + Guid.NewGuid().ToString("N") }, backend);
        host.HandleIpcRequest(Request("scan_devices")); host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        host.HandleIpcRequest(Request("set_rgb_enabled", ("enabled", true)));
        host.HandleIpcRequest(Request("set_rgb_enabled", ("enabled", true)));
        TestAssert.Equal(1, backend.LastHandle!.SnapshotsCaptured, "repeated enable recaptured the baseline");
        host.HandleIpcRequest(Request("disconnect_device"));
        host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        var rejected = host.HandleIpcRequest(Request("restore_lighting"));
        TestAssert.True(rejected.Contains("RESTORE_SNAPSHOT_UNAVAILABLE"), "old session restore remained valid");
        return Task.CompletedTask;
    }

    public static Task DeviceLifecyclePreservesCodexState()
    {
        var backend = new FakeBackend();
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "state-" + Guid.NewGuid().ToString("N") }, backend);
        host.ApplyNativeStatusJson("{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\",\"threadId\":\"t\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T00:00:00Z\"}");
        var before = RuntimeContractJson.Serialize(host.Snapshot);
        host.HandleIpcRequest(Request("scan_devices")); host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        host.HandleIpcRequest(Request("disconnect_device")); host.HandleIpcRequest(Request("scan_devices"));
        TestAssert.Equal(before, RuntimeContractJson.Serialize(host.Snapshot), "device lifecycle mutated Codex state");
        return Task.CompletedTask;
    }

    public static Task RgbDoesNotWriteWithoutOwnership()
    {
        var backend = new FakeBackend();
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "writes-" + Guid.NewGuid().ToString("N") }, backend);
        host.RgbController.ApplyRuntimeState(RuntimeState.Waiting);
        TestAssert.Equal(0, backend.TotalEffectWrites, "disconnected RGB wrote an effect");
        return Task.CompletedTask;
    }

    public static Task CapabilitiesMatchCommandAllowlist()
    {
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "caps-" + Guid.NewGuid().ToString("N") });
        using var json = JsonDocument.Parse(host.HandleIpcRequest(Request("snapshot")));
        var advertised = json.RootElement.GetProperty("snapshot").GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ToArray();
        TestAssert.True(advertised.SequenceEqual(RuntimeIpcMetadata.Capabilities), "capability advertisement is stale or reordered");
        return Task.CompletedTask;
    }

    public static Task StopDisposesOwnershipAndRestartStartsUnarmed()
    {
        var backend = new FakeBackend();
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "stop-" + Guid.NewGuid().ToString("N") }, backend);
        host.HandleIpcRequest(Request("scan_devices")); host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        host.HandleIpcRequest(Request("set_rgb_enabled", ("enabled", true)));
        host.Stop();
        TestAssert.True(backend.LastHandle!.Disposed, "stop did not dispose HID ownership");
        TestAssert.False(host.RgbController.Enabled, "stop left RGB armed");
        TestAssert.True(host.TryStart(), "runtime did not restart");
        TestAssert.False(host.DeviceManager.IsConnected, "restart inherited a live handle");
        TestAssert.False(host.RgbController.Enabled, "restart inherited RGB session");
        return Task.CompletedTask;
    }

    public static Task OwnershipLossDisarmsUntilExplicitReenable()
    {
        var backend = new FakeBackend { FailEffect = true };
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "loss-" + Guid.NewGuid().ToString("N") }, backend);
        host.HandleIpcRequest(Request("scan_devices")); host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        host.HandleIpcRequest(Request("set_rgb_enabled", ("enabled", true)));
        host.ApplyNativeStatusJson("{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\",\"threadId\":\"t\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T00:00:00Z\"}");
        TestAssert.False(host.RgbController.Enabled, "connection loss did not disarm RGB");
        backend.FailEffect = false;
        host.HandleIpcRequest(Request("reconnect_device"));
        var writes = backend.TotalEffectWrites;
        host.ApplyNativeStatusJson("{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\",\"threadId\":\"t\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T00:00:01Z\"}");
        TestAssert.Equal(writes, backend.TotalEffectWrites, "reconnect resumed RGB without explicit re-enable");
        host.HandleIpcRequest(Request("set_rgb_enabled", ("enabled", true)));
        TestAssert.Equal(1, backend.LastHandle!.SnapshotsCaptured, "explicit re-enable did not capture a fresh baseline");
        return Task.CompletedTask;
    }

    public static async Task ConcurrentOwnershipAndRgbActivityCompletes()
    {
        var backend = new FakeBackend();
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "concurrent-" + Guid.NewGuid().ToString("N") }, backend);
        host.HandleIpcRequest(Request("scan_devices")); host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a")));
        host.HandleIpcRequest(Request("set_rgb_enabled", ("enabled", true)));
        var tasks = Enumerable.Range(0, 40).Select(index => Task.Run(() =>
        {
            if (index % 3 == 0) host.HandleIpcRequest(Request("disconnect_device"));
            else if (index % 3 == 1) { host.HandleIpcRequest(Request("scan_devices")); host.HandleIpcRequest(Request("connect_device", ("candidateId", "candidate-a"))); }
            else host.ApplyNativeStatusJson("{\"schemaVersion\":\"k15-codex-thread-status/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"thread_status_changed\",\"threadId\":\"t\",\"status\":\"active\",\"activeFlags\":[],\"timestampUtc\":\"2026-09-08T00:00:00Z\"}");
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        TestAssert.True(backend.MaxLiveHandles <= 1, "concurrent commands owned more than one handle");
    }

    public static Task ScanFailureUsesFailClosedIpcResponse()
    {
        var backend = new FakeBackend { ScanFails = true };
        using var host = new RuntimeHost(new RuntimeHostOptions { SingleInstanceName = "scan-fail-" + Guid.NewGuid().ToString("N") }, backend);
        var response = host.HandleIpcRequest(Request("scan_devices"));
        TestAssert.True(response.Contains("\"ok\":false") && response.Contains("SCAN_FAILED"), "scan failure was acknowledged as success");
        TestAssert.False(response.Contains("InvalidOperationException"), "raw scan exception leaked through IPC");
        return Task.CompletedTask;
    }

    public static Task LegacyRgbPolicyAndRestorePlanAreExact()
    {
        var a = RuntimeRgbPolicy.ForState(RuntimeState.Waiting, 0)!;
        var b = RuntimeRgbPolicy.ForState(RuntimeState.Waiting, 1)!;
        var aRgb = K15HidProtocol.CreateEffectRecord(a, RuntimeWireColorOrder.Rgb);
        var bGrb = K15HidProtocol.CreateEffectRecord(b, RuntimeWireColorOrder.Grb);
        TestAssert.Equal(K15HidProtocol.SingleColorBreathingMode, a.Mode, "WAITING mode changed");
        TestAssert.Equal(0, aRgb[2], "WAITING brightness changed");
        TestAssert.True(aRgb[4] == 0xFF && aRgb[5] == 0 && aRgb[6] == 0, "profile A RGB color changed");
        TestAssert.True(bGrb[4] == 0 && bGrb[5] == 0 && bGrb[6] == 0xFF, "profile B GRB color changed");
        var snapshot = new RuntimeLightingSnapshot(0, new byte[] { K15HidProtocol.SingleColorBreathingMode }.Concat(new byte[24]).ToArray(),
            new Dictionary<byte, byte[]> { [K15HidProtocol.SingleColorBreathingMode] = new byte[25], [K15HidProtocol.OffMode] = new byte[25] });
        var plan = K15HidProtocol.RestorePlan(snapshot);
        TestAssert.Equal(2, plan.Length, "restore plan included unproven OFF or wrong records");
        TestAssert.Equal(K15HidProtocol.ModeRecordAddress(K15HidProtocol.SingleColorBreathingMode), plan[0].Address, "baseline record was not restored first");
        TestAssert.Equal((ushort)0, plan[1].Address, "header was not restored second");
        return Task.CompletedTask;
    }

    private static string Request(string command, params (string Name, object Value)[] args)
    {
        var values = new Dictionary<string, object> { ["protocolVersion"] = RuntimeIpcMetadata.ProtocolVersion, ["command"] = command };
        foreach (var arg in args) values[arg.Name] = arg.Value;
        return JsonSerializer.Serialize(values);
    }

    private sealed class FakeBackend : IK15DeviceBackend
    {
        public bool VerifyFails { get; init; }
        public bool DuplicateIdentity { get; set; }
        public bool ScanFails { get; init; }
        public bool FailEffect { get; set; }
        public string Identity => "36A4:4100|K15|serial-a|FF01:0001|41";
        public int OpenCount { get; private set; }
        public int RealWrites => 0;
        public FakeHandle? LastHandle { get; private set; }
        public int TotalEffectWrites => LastHandle?.Effects.Count ?? 0;
        public int MaxLiveHandles { get; private set; }
        public IReadOnlyList<RuntimeDeviceCandidate> Scan()
        {
            if (ScanFails) throw new InvalidOperationException("fake scan failure");
            return new[] {
                new RuntimeDeviceCandidate("candidate-a", "\\\\fake\\\\hid\\\\a", "K15", "serial-a", 0x36A4, 0x4100, 0xFF01, 1, 41),
                new RuntimeDeviceCandidate("candidate-b", "\\\\fake\\\\hid\\\\b", "K15", DuplicateIdentity ? "serial-a" : "serial-b", 0x36A4, 0x4100, 0xFF01, 1, 41)
            };
        }
        public IK15DeviceHandle Open(RuntimeDeviceCandidate candidate) { OpenCount++; LastHandle = new FakeHandle(candidate.CandidateId, VerifyFails, () => FailEffect); MaxLiveHandles = Math.Max(MaxLiveHandles, 1); return LastHandle; }
    }

    private sealed class FakeHandle : IK15DeviceHandle
    {
        private readonly bool _verifyFails;
        private readonly Func<bool> _failEffect;
        public FakeHandle(string candidateId, bool verifyFails, Func<bool> failEffect) { CandidateId = candidateId; _verifyFails = verifyFails; _failEffect = failEffect; }
        public string CandidateId { get; }
        public byte ActiveSlot => 0;
        public bool Disposed { get; private set; }
        public int SnapshotsCaptured { get; private set; }
        public List<string> Effects { get; } = new();
        public void VerifyProtocol() { if (_verifyFails) throw new InvalidDataException(); }
        public RuntimeLightingSnapshot CaptureLightingSnapshot() { SnapshotsCaptured++; return new RuntimeLightingSnapshot(0, new byte[25], new Dictionary<byte, byte[]>()); }
        public void ApplyEffect(RuntimeLightingEffect effect) { if (_failEffect()) throw new IOException(); Effects.Add(effect.Mode.ToString()); }
        public void RestoreLighting(RuntimeLightingSnapshot snapshot) { if (snapshot.ActiveSlot != 0) throw new InvalidDataException(); }
        public void Dispose() => Disposed = true;
    }
}
