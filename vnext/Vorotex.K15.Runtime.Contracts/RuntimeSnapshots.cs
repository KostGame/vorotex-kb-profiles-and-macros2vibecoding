using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Vorotex.K15.Runtime.Contracts;

public sealed record ThreadSnapshot
{
    [JsonConstructor]
    public ThreadSnapshot(
        string threadId,
        ThreadRuntimeStatus runtimeStatus,
        ImmutableArray<ThreadActiveFlag> activeFlags = default,
        DateTimeOffset? lastObservedUtc = null,
        RuntimeState state = RuntimeState.Unknown,
        ThreadAttentionState attention = ThreadAttentionState.Unknown,
        DateTimeOffset? lastAttentionObservedUtc = null,
        ThreadClassification classification = ThreadClassification.User)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ThreadId = threadId;
        RuntimeStatus = runtimeStatus;
        ActiveFlags = CanonicalizeActiveFlags(activeFlags);
        LastObservedUtc = lastObservedUtc;
        State = state;
        Attention = attention;
        LastAttentionObservedUtc = lastAttentionObservedUtc;
        Classification = classification;
    }

    [JsonPropertyOrder(0)]
    public string ThreadId { get; }

    [JsonPropertyOrder(1)]
    public ThreadRuntimeStatus RuntimeStatus { get; }

    [JsonPropertyOrder(2)]
    public ImmutableArray<ThreadActiveFlag> ActiveFlags { get; }

    [JsonPropertyOrder(3)]
    public DateTimeOffset? LastObservedUtc { get; }

    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RuntimeState State { get; }

    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ThreadAttentionState Attention { get; }

    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastAttentionObservedUtc { get; }

    [JsonPropertyOrder(7)]
    public ThreadClassification Classification { get; }

    public static ThreadSnapshot Create(
        string threadId,
        ThreadRuntimeStatus runtimeStatus,
        IEnumerable<ThreadActiveFlag>? activeFlags = null,
        DateTimeOffset? lastObservedUtc = null,
        RuntimeState state = RuntimeState.Unknown,
        ThreadAttentionState attention = ThreadAttentionState.Unknown,
        DateTimeOffset? lastAttentionObservedUtc = null,
        ThreadClassification classification = ThreadClassification.User)
    {
        return new ThreadSnapshot(
            threadId,
            runtimeStatus,
            activeFlags?.ToImmutableArray() ?? ImmutableArray<ThreadActiveFlag>.Empty,
            lastObservedUtc,
            state,
            attention,
            lastAttentionObservedUtc,
            classification);
    }

    private static ImmutableArray<ThreadActiveFlag> CanonicalizeActiveFlags(
        ImmutableArray<ThreadActiveFlag> activeFlags)
    {
        if (activeFlags.IsDefaultOrEmpty)
        {
            return ImmutableArray<ThreadActiveFlag>.Empty;
        }

        return activeFlags
            .OrderBy(GetActiveFlagOrder)
            .ThenBy(flag => (int)flag)
            .ToImmutableArray();
    }

    private static int GetActiveFlagOrder(ThreadActiveFlag activeFlag) => activeFlag switch
    {
        ThreadActiveFlag.WaitingOnApproval => 0,
        ThreadActiveFlag.WaitingOnUserInput => 1,
        ThreadActiveFlag.Unknown => 2,
        _ => 3,
    };
}

public sealed record RuntimeHealthSnapshot(
    [property: JsonPropertyOrder(0)] string RuntimeVersion,
    [property: JsonPropertyOrder(1)] bool IsHealthy,
    [property: JsonPropertyOrder(2)] string Detail)
{
    public static RuntimeHealthSnapshot Healthy(
        string runtimeVersion = RuntimeContractMetadata.CurrentRuntimeVersion) =>
        new(runtimeVersion, IsHealthy: true, Detail: "READY");

    public static RuntimeHealthSnapshot Degraded(string runtimeVersion, string reason) =>
        new(runtimeVersion, IsHealthy: false, Detail: reason);
}

public sealed record RuntimeSnapshot(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] RuntimeState State,
    [property: JsonPropertyOrder(2)] ImmutableArray<ThreadSnapshot> Threads,
    [property: JsonPropertyOrder(3)] RuntimeHealthSnapshot Health)
{
    public static RuntimeSnapshot CreateInitial(
        string runtimeVersion = RuntimeContractMetadata.CurrentRuntimeVersion) =>
        new(
            SchemaVersion: RuntimeContractMetadata.CurrentSchemaVersion,
            State: RuntimeState.Normal,
            Threads: ImmutableArray<ThreadSnapshot>.Empty,
            Health: RuntimeHealthSnapshot.Healthy(runtimeVersion));
}
