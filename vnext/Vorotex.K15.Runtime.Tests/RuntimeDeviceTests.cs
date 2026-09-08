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
        TestAssert.Equal("WAITING", backend.LastHandle!.Effects[^1], "canonical state did not choose deterministic effect");
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
        public string Identity => "36A4:4100|K15|serial-a|FF01:0001|41";
        public int OpenCount { get; private set; }
        public int RealWrites => 0;
        public FakeHandle? LastHandle { get; private set; }
        public int TotalEffectWrites => LastHandle?.Effects.Count ?? 0;
        public IReadOnlyList<RuntimeDeviceCandidate> Scan() => new[] {
            new RuntimeDeviceCandidate("candidate-a", "\\\\fake\\\\hid\\\\a", "K15", "serial-a", 0x36A4, 0x4100, 0xFF01, 1, 41),
            new RuntimeDeviceCandidate("candidate-b", "\\\\fake\\\\hid\\\\b", "K15", DuplicateIdentity ? "serial-a" : "serial-b", 0x36A4, 0x4100, 0xFF01, 1, 41)
        };
        public IK15DeviceHandle Open(RuntimeDeviceCandidate candidate) { OpenCount++; LastHandle = new FakeHandle(candidate.CandidateId, VerifyFails); return LastHandle; }
    }

    private sealed class FakeHandle : IK15DeviceHandle
    {
        private readonly bool _verifyFails;
        public FakeHandle(string candidateId, bool verifyFails) { CandidateId = candidateId; _verifyFails = verifyFails; }
        public string CandidateId { get; }
        public bool Disposed { get; private set; }
        public int SnapshotsCaptured { get; private set; }
        public List<string> Effects { get; } = new();
        public void VerifyProtocol() { if (_verifyFails) throw new InvalidDataException(); }
        public byte[] CaptureLightingSnapshot() { SnapshotsCaptured++; return new byte[] { 1, 2, 3 }; }
        public void ApplyEffect(string effect) => Effects.Add(effect);
        public void RestoreLighting(byte[] snapshot) { if (snapshot.Length != 3) throw new InvalidDataException(); }
        public void Dispose() => Disposed = true;
    }
}
