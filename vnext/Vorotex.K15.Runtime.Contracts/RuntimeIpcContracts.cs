using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Vorotex.K15.Runtime.Contracts;

public static class RuntimeIpcMetadata
{
    public const string ProtocolVersion = "k15-runtime-ipc/v1";
    public const int MaxFrameBytes = 16 * 1024;
}

public sealed record RuntimeIpcRequest(
    [property: JsonPropertyOrder(0)] string ProtocolVersion,
    [property: JsonPropertyOrder(1)] string Command);

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

public sealed record RuntimeIpcDevicePlaceholder([property: JsonPropertyOrder(0)] string Status);

public sealed record RuntimeIpcSnapshot(
    [property: JsonPropertyOrder(0)] string ProtocolVersion,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] RuntimeIpcRuntimeHealth Runtime,
    [property: JsonPropertyOrder(3)] RuntimeState State,
    [property: JsonPropertyOrder(4)] ImmutableArray<ThreadSnapshot> Threads,
    [property: JsonPropertyOrder(5)] RuntimeIpcRuntimeHealth NativeAuthority,
    [property: JsonPropertyOrder(6)] RuntimeIpcAttention Attention,
    [property: JsonPropertyOrder(7)] string? FocusedThreadId,
    [property: JsonPropertyOrder(8)] RuntimeIpcDevicePlaceholder Device,
    [property: JsonPropertyOrder(9)] RuntimeIpcDevicePlaceholder Rgb,
    [property: JsonPropertyOrder(10)] ImmutableArray<string> Capabilities)
{
    public static RuntimeIpcSnapshot From(RuntimeSnapshot snapshot, RuntimeIpcRuntimeHealth runtimeHealth)
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
            focusedThreadId, new RuntimeIpcDevicePlaceholder("NOT_IMPLEMENTED"),
            new RuntimeIpcDevicePlaceholder("NOT_IMPLEMENTED"), ImmutableArray.Create("ping", "snapshot"));
    }
}
