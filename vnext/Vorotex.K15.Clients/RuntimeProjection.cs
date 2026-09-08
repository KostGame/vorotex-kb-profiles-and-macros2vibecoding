using System.Collections.Immutable;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Clients;

public sealed record RuntimeThreadProjection(string ThreadId, RuntimeState State, ThreadClassification Classification, ThreadFocusHint FocusHint, DateTimeOffset? LastObservedUtc, string? WorkingDirectory);
public sealed record RuntimeHealthProjection(string Version, bool IsHealthy, string Detail);
public sealed record RuntimeClientProjection(
    bool Online, string Health, RuntimeHealthProjection RuntimeHealth, RuntimeHealthProjection NativeAuthorityHealth, RuntimeState State, int ActiveCount, int RunningCount, int WaitingCount, int DoneCount,
    ImmutableArray<RuntimeThreadProjection> Threads, RuntimeIpcDeviceSnapshot Device, RuntimeIpcRgbSnapshot Rgb, string? FocusedThreadId)
{
    public bool AuthorityHealthy => RuntimeHealth.IsHealthy && NativeAuthorityHealth.IsHealthy;
    public bool Degraded => !AuthorityHealthy;
    public static RuntimeClientProjection Offline(string health = "RUNTIME_OFFLINE") => new(false, health,
        new("UNKNOWN", false, health), new("UNKNOWN", false, health), RuntimeState.Unknown, 0, 0, 0, 0, ImmutableArray<RuntimeThreadProjection>.Empty,
        new("DISCONNECTED", null, ImmutableArray<RuntimeIpcDeviceCandidate>.Empty, 0, false, null), new(false, "NORMAL", false, false, null), null);
}

public static class RuntimeProjection
{
    public static RuntimeClientProjection From(RuntimeIpcSnapshot? snapshot, bool includeDiagnostics = false)
    {
        if (snapshot is null) return RuntimeClientProjection.Offline();
        var threads = (snapshot.Threads.IsDefault ? ImmutableArray<ThreadSnapshot>.Empty : snapshot.Threads)
            .Where(thread => includeDiagnostics || thread.Classification == ThreadClassification.User)
            .Select(thread => new RuntimeThreadProjection(thread.ThreadId, thread.State, thread.Classification, thread.FocusHint, thread.LastObservedUtc, thread.WorkingDirectory))
            .ToImmutableArray();
        var runtimeHealth = new RuntimeHealthProjection(snapshot.Runtime.Version, snapshot.Runtime.IsHealthy, snapshot.Runtime.Detail);
        var nativeHealth = new RuntimeHealthProjection(snapshot.NativeAuthority.Version, snapshot.NativeAuthority.IsHealthy, snapshot.NativeAuthority.Detail);
        return new(snapshot.Runtime.IsHealthy, snapshot.Runtime.IsHealthy ? "READY" : snapshot.Runtime.Detail, runtimeHealth, nativeHealth, snapshot.State,
            threads.Count(t => t.State is RuntimeState.Running or RuntimeState.Waiting),
            threads.Count(t => t.State == RuntimeState.Running), threads.Count(t => t.State == RuntimeState.Waiting),
            threads.Count(t => t.State == RuntimeState.DonePendingAttention), threads, snapshot.Device, snapshot.Rgb, snapshot.FocusedThreadId);
    }
}
