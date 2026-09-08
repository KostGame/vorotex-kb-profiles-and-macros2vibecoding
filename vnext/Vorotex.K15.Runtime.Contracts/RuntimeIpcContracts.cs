using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Vorotex.K15.Runtime.Contracts;

public static class RuntimeIpcMetadata
{
    public const string ProtocolVersion = "k15-runtime-ipc/v1";
    public const int MaxFrameBytes = 16 * 1024;
    public const string NativeAuthorityPipeName = "Vorotex.K15.Runtime.NativeAuthority.v1";
    public const string NativeAuthorityStatusSchema = "k15-codex-thread-status/v1";
    public const string NativeAuthorityMetadataSchema = "k15-codex-thread-metadata/v1";
    public const string NativeAuthorityHealthSchema = "k15-codex-authority-health/v1";
    public static readonly ImmutableArray<string> Capabilities = ImmutableArray.Create(
        "ping", "snapshot", "scan_devices", "connect_device", "disconnect_device",
        "reconnect_device", "set_rgb_enabled", "restore_lighting");
}

public sealed record RuntimeIpcRequest(
    [property: JsonPropertyOrder(0)] string ProtocolVersion,
    [property: JsonPropertyOrder(1)] string Command,
    [property: JsonPropertyOrder(2)] string? CandidateId = null,
    [property: JsonPropertyOrder(3)] bool? Enabled = null);

public sealed record RuntimeIpcResponse(
    [property: JsonPropertyOrder(0)] string ProtocolVersion,
    [property: JsonPropertyOrder(1)] bool Ok,
    [property: JsonPropertyOrder(2)] string Command,
    [property: JsonPropertyOrder(3)] RuntimeIpcSnapshot? Snapshot = null,
    [property: JsonPropertyOrder(4)] string? Error = null);

public sealed record RuntimeIpcRuntimeHealth(
    [property: JsonPropertyOrder(0)] string Version,
    [property: JsonPropertyOrder(1)] bool IsHealthy,
    [property: JsonPropertyOrder(2)] string Detail);

public sealed record RuntimeIpcAttention([property: JsonPropertyOrder(0)] int UnreadCount);

public sealed record RuntimeIpcDeviceCandidate(
    [property: JsonPropertyOrder(0)] string CandidateId,
    [property: JsonPropertyOrder(1)] string Product,
    [property: JsonPropertyOrder(2)] int VendorId,
    [property: JsonPropertyOrder(3)] int ProductId,
    [property: JsonPropertyOrder(4)] bool? ProtocolVerified,
    [property: JsonPropertyOrder(5)] bool Selected,
    [property: JsonPropertyOrder(6)] bool Connected);

public sealed record RuntimeIpcDeviceSnapshot(
    [property: JsonPropertyOrder(0)] string ConnectionState,
    [property: JsonPropertyOrder(1)] string? SelectedCandidateId,
    [property: JsonPropertyOrder(2)] ImmutableArray<RuntimeIpcDeviceCandidate> Candidates,
    [property: JsonPropertyOrder(3)] int CandidateCount,
    [property: JsonPropertyOrder(4)] bool ProtocolVerified,
    [property: JsonPropertyOrder(5)] string? LastFailure);

public sealed record RuntimeIpcRgbSnapshot(
    [property: JsonPropertyOrder(0)] bool Enabled,
    [property: JsonPropertyOrder(1)] string Effect,
    [property: JsonPropertyOrder(2)] bool TransportAvailable,
    [property: JsonPropertyOrder(3)] bool RestoreSnapshotAvailable,
    [property: JsonPropertyOrder(4)] string? LastFailure);

public sealed record RuntimeIpcSnapshot(
    [property: JsonPropertyOrder(0)] string ProtocolVersion,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] RuntimeIpcRuntimeHealth Runtime,
    [property: JsonPropertyOrder(3)] RuntimeState State,
    [property: JsonPropertyOrder(4)] ImmutableArray<ThreadSnapshot> Threads,
    [property: JsonPropertyOrder(5)] RuntimeIpcRuntimeHealth NativeAuthority,
    [property: JsonPropertyOrder(6)] RuntimeIpcAttention Attention,
    [property: JsonPropertyOrder(7)] string? FocusedThreadId,
    [property: JsonPropertyOrder(8)] RuntimeIpcDeviceSnapshot Device,
    [property: JsonPropertyOrder(9)] RuntimeIpcRgbSnapshot Rgb,
    [property: JsonPropertyOrder(10)] ImmutableArray<string> Capabilities)
{
    public static RuntimeIpcSnapshot From(RuntimeSnapshot snapshot, RuntimeIpcRuntimeHealth runtimeHealth,
        RuntimeIpcDeviceSnapshot? device = null, RuntimeIpcRgbSnapshot? rgb = null,
        IEnumerable<string>? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(runtimeHealth);
        var threads = snapshot.Threads.IsDefault ? ImmutableArray<ThreadSnapshot>.Empty : snapshot.Threads;
        var focusedThreadId = threads
            .Where(thread => thread.FocusHint == ThreadFocusHint.Focused)
            .Select(thread => thread.ThreadId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();
        var nativeHealth = new RuntimeIpcRuntimeHealth(snapshot.Health.RuntimeVersion, snapshot.Health.IsHealthy, snapshot.Health.Detail);
        return new RuntimeIpcSnapshot(
            RuntimeIpcMetadata.ProtocolVersion, snapshot.SchemaVersion, runtimeHealth, snapshot.State, threads, nativeHealth,
            new RuntimeIpcAttention(threads.Count(thread => thread.Attention == ThreadAttentionState.Unread)),
            focusedThreadId,
            device ?? new RuntimeIpcDeviceSnapshot("DISCONNECTED", null, ImmutableArray<RuntimeIpcDeviceCandidate>.Empty, 0, false, null),
            rgb ?? new RuntimeIpcRgbSnapshot(false, "NORMAL", false, false, null),
            (capabilities ?? RuntimeIpcMetadata.Capabilities).ToImmutableArray());
    }
}
