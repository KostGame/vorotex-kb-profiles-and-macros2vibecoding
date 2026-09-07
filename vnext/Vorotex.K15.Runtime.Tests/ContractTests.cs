using System.Text.Json;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime.Tests;

internal static class ContractTests
{
    public static Task InitialSnapshotIsDeterministicAndHealthy()
    {
        var first = RuntimeSnapshot.CreateInitial();
        var second = RuntimeSnapshot.CreateInitial();
        var json = RuntimeContractJson.Serialize(first);

        TestAssert.Equal(
            RuntimeContractJson.Serialize(second),
            json,
            "initial snapshot serialization changed between identical constructions");
        TestAssert.Equal(
            "{\"schemaVersion\":1,\"state\":\"NORMAL\",\"threads\":[],\"health\":{\"runtimeVersion\":\"0.1.0-vnext\",\"isHealthy\":true,\"detail\":\"READY\"}}",
            json,
            "initial snapshot wire shape is not stable");
        TestAssert.Equal(RuntimeState.Normal, first.State, "initial state must be NORMAL");
        TestAssert.True(first.Threads.IsEmpty, "initial snapshot must have no threads");
        TestAssert.True(first.Health.IsHealthy, "initial health must be healthy");
        TestAssert.Equal("0.1.0-vnext", first.Health.RuntimeVersion, "initial version changed");

        var roundTrip = RuntimeContractJson.Deserialize<RuntimeSnapshot>(json);
        TestAssert.NotNull(roundTrip, "initial snapshot did not deserialize");
        TestAssert.Equal(RuntimeState.Normal, roundTrip!.State, "round-trip state changed");
        TestAssert.True(roundTrip.Threads.IsEmpty, "round-trip thread list is not empty");
        TestAssert.True(roundTrip.Health.IsHealthy, "round-trip health changed");
        return Task.CompletedTask;
    }

    public static Task EnumWireNamesAreExplicitAndStable()
    {
        var options = RuntimeContractJson.CreateOptions();

        TestAssert.Equal("\"NORMAL\"", JsonSerializer.Serialize(RuntimeState.Normal, options), "NORMAL wire name changed");
        TestAssert.Equal("\"DONE_PENDING_ATTENTION\"", JsonSerializer.Serialize(RuntimeState.DonePendingAttention, options), "DONE wire name changed");
        TestAssert.Equal("\"NOT_LOADED\"", JsonSerializer.Serialize(ThreadRuntimeStatus.NotLoaded, options), "NOT_LOADED wire name changed");
        TestAssert.Equal("\"SYSTEM_ERROR\"", JsonSerializer.Serialize(ThreadRuntimeStatus.SystemError, options), "SYSTEM_ERROR wire name changed");
        TestAssert.Equal("\"WAITING_ON_APPROVAL\"", JsonSerializer.Serialize(ThreadActiveFlag.WaitingOnApproval, options), "approval wire name changed");
        TestAssert.Equal("\"WAITING_ON_USER_INPUT\"", JsonSerializer.Serialize(ThreadActiveFlag.WaitingOnUserInput, options), "input wire name changed");
        TestAssert.Equal("\"UNKNOWN\"", JsonSerializer.Serialize((RuntimeState)999, options), "unknown enum serialization is not fail-closed");
        return Task.CompletedTask;
    }

    public static Task UnknownWireValuesFailClosed()
    {
        const string futureJson = "{\"schemaVersion\":1,\"state\":\"FUTURE_STATE\",\"threads\":[{\"threadId\":\"future-thread\",\"runtimeStatus\":\"FUTURE_STATUS\",\"activeFlag\":\"FUTURE_FLAG\"}],\"health\":{\"runtimeVersion\":\"0.1.0-vnext\",\"isHealthy\":true,\"detail\":\"READY\"}}";
        var future = RuntimeContractJson.Deserialize<RuntimeSnapshot>(futureJson);

        TestAssert.NotNull(future, "future wire values should deserialize");
        TestAssert.Equal(RuntimeState.Unknown, future!.State, "future runtime state must not become healthy NORMAL");
        TestAssert.Equal(1, future.Threads.Length, "future thread was lost");
        TestAssert.Equal(ThreadRuntimeStatus.Unknown, future.Threads[0].RuntimeStatus, "future thread status must be UNKNOWN");
        TestAssert.Equal(ThreadActiveFlag.Unknown, future.Threads[0].ActiveFlag, "future active flag must be UNKNOWN");

        const string invalidJson = "{\"schemaVersion\":1,\"state\":999,\"threads\":[],\"health\":{\"runtimeVersion\":\"0.1.0-vnext\",\"isHealthy\":true,\"detail\":\"READY\"}}";
        var invalid = RuntimeContractJson.Deserialize<RuntimeSnapshot>(invalidJson);
        TestAssert.NotNull(invalid, "invalid enum token should not abort snapshot parsing");
        TestAssert.Equal(RuntimeState.Unknown, invalid!.State, "invalid numeric state must be UNKNOWN");

        var reserialized = RuntimeContractJson.Serialize(future);
        TestAssert.True(reserialized.Contains("\"state\":\"UNKNOWN\"", StringComparison.Ordinal), "unknown state was not preserved fail-closed");
        TestAssert.False(reserialized.Contains("\"state\":\"NORMAL\"", StringComparison.Ordinal), "unknown state silently became NORMAL");
        return Task.CompletedTask;
    }
}
