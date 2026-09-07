using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Vorotex.K15.Runtime.Contracts;

public sealed record ThreadSnapshot(
    [property: JsonPropertyOrder(0)] string ThreadId,
    [property: JsonPropertyOrder(1)] ThreadRuntimeStatus RuntimeStatus,
    [property: JsonPropertyOrder(2)] ThreadActiveFlag? ActiveFlag = null,
    [property: JsonPropertyOrder(3)] DateTimeOffset? LastObservedUtc = null)
{
    public static ThreadSnapshot Create(
        string threadId,
        ThreadRuntimeStatus runtimeStatus,
        ThreadActiveFlag? activeFlag = null,
        DateTimeOffset? lastObservedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return new ThreadSnapshot(threadId, runtimeStatus, activeFlag, lastObservedUtc);
    }
}

public sealed record RuntimeHealthSnapshot(
    [property: JsonPropertyOrder(0)] string RuntimeVersion,
    [property: JsonPropertyOrder(1)] bool IsHealthy,
    [property: JsonPropertyOrder(2)] string Detail)
{
    public static RuntimeHealthSnapshot Healthy(
        string runtimeVersion = RuntimeContractMetadata.CurrentRuntimeVersion) =>
        new(runtimeVersion, IsHealthy: true, Detail: "READY");
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
