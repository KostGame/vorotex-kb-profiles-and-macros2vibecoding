namespace Vorotex.K15.Runtime.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("initial snapshot is deterministic and healthy", ContractTests.InitialSnapshotIsDeterministicAndHealthy),
            ("enum wire names are explicit and stable", ContractTests.EnumWireNamesAreExplicitAndStable),
            ("thread classification wire names are stable and fail closed", ContractTests.ThreadClassificationWireNamesAreStableAndFailClosed),
            ("active flags are plural immutable and deterministic", ContractTests.ActiveFlagsArePluralImmutableAndDeterministic),
            ("unknown wire values fail closed", ContractTests.UnknownWireValuesFailClosed),
            ("host enforces single instance ownership", RuntimeHostTests.HostEnforcesSingleInstanceOwnership),
            ("host shuts down through cancellation", RuntimeHostTests.HostShutsDownThroughCancellation),
            ("state engine maps native status and attention evidence", RuntimeStateEngineTests.MapsNativeStatusAndAttentionEvidence),
            ("state engine preserves owner-live sticky waiting regression", RuntimeStateEngineTests.OwnerLiveStickyWaitingRegression),
            ("state engine applies exact aggregate priority", RuntimeStateEngineTests.AggregatePriorityIsExactAndDeterministic),
            ("state engine excludes not-loaded threads from live aggregate", RuntimeStateEngineTests.NotLoadedDoesNotPoisonAggregate),
            ("state engine rejects stale duplicate and legacy inputs", RuntimeStateEngineTests.StaleDuplicateAndLegacyInputsFailClosed),
            ("state engine keeps focus orthogonal and rehydrates deterministically", RuntimeStateEngineTests.FocusIsOrthogonalAndRehydrationIsDeterministic),
            ("native adapter parses bounded status metadata", NativeThreadStatusAdapterTests.ParsesBoundedNativeStatus),
            ("native adapter preserves plural flags and classifications", NativeThreadStatusAdapterTests.PreservesFlagsAndClassifications),
            ("native adapter fails closed for invalid payloads", NativeThreadStatusAdapterTests.FailsClosed),
            ("native adapter feeds the real state engine lifecycle", NativeThreadStatusAdapterTests.FeedsRuntimeLifecycle),
            ("native adapter accepts exact non-active union shapes", NativeThreadStatusAdapterTests.AcceptsExactNonActiveUnionShapes),
            ("bridge health degrades canonical runtime snapshot", NativeThreadStatusAdapterTests.BridgeHealthDegradesCanonicalRuntimeSnapshot),
            ("native transport preserves ordered authority and health", NativeThreadStatusAdapterTests.TransportPreservesOrderedAuthorityAndHealth),
            ("native queue has explicit concurrent overflow health", NativeThreadStatusAdapterTests.ConcurrentQueueNeverReportsAcceptedLoss),
            ("IPC ping wire shape is exact", RuntimeIpcTests.PingWireShapeIsExact),
            ("IPC snapshot is canonical and stable", RuntimeIpcTests.SnapshotIsCanonicalAndStable),
            ("IPC oversized snapshot returns bounded error", RuntimeIpcTests.OversizedSnapshotReturnsBoundedError),
            ("IPC runtime process health reflects host state", RuntimeIpcTests.RuntimeProcessHealthReflectsHostState),
            ("IPC invalid requests fail closed", RuntimeIpcTests.InvalidRequestsFailClosedWithoutProviderAccess),
            ("IPC oversized and reserved commands are bounded", RuntimeIpcTests.OversizedAndReservedCommandsAreBounded),
            ("IPC named pipe supports concurrent clients and restart", RuntimeIpcTests.NamedPipeSupportsConcurrentClientsAndRestart),
            ("device manager requires explicit selection and owns one handle", RuntimeDeviceTests.DeviceManagerRequiresExplicitSelectionAndOwnsOneHandle),
            ("protocol verification failure disposes pending ownership", RuntimeDeviceTests.ProtocolVerificationFailureFailsClosed),
            ("RGB follows normalized state only through fake transport", RuntimeDeviceTests.RgbUsesFakeTransportAndPreservesStateAuthority),
            ("IPC device commands are bounded and private", RuntimeDeviceTests.IpcCommandsAreBoundedAndDoNotExposePaths),
            ("preferred endpoint identity is exact and fails closed", RuntimeDeviceTests.PreferredIdentityFailsClosed),
            ("restore is session-bound and repeated enable preserves baseline", RuntimeDeviceTests.RestoreIsSessionBound),
            ("device lifecycle never mutates Codex aggregate state", RuntimeDeviceTests.DeviceLifecyclePreservesCodexState),
            ("RGB never writes while disabled or disconnected", RuntimeDeviceTests.RgbDoesNotWriteWithoutOwnership),
            ("capabilities advertise exactly the accepted commands", RuntimeDeviceTests.CapabilitiesMatchCommandAllowlist),
            ("stop disposes ownership and restart starts unarmed", RuntimeDeviceTests.StopDisposesOwnershipAndRestartStartsUnarmed),
            ("ownership loss disarms RGB until explicit re-enable", RuntimeDeviceTests.OwnershipLossDisarmsUntilExplicitReenable),
            ("concurrent ownership and RGB activity is deadlock-free", RuntimeDeviceTests.ConcurrentOwnershipAndRgbActivityCompletes),
            ("scan failure uses fail-closed IPC response", RuntimeDeviceTests.ScanFailureUsesFailClosedIpcResponse),
            ("legacy RGB policy and restore plan are exact", RuntimeDeviceTests.LegacyRgbPolicyAndRestorePlanAreExact),
            ("restore runs before disable and stop", RuntimeDeviceTests.RestoreRunsBeforeDisableAndStop),
            ("restore failure still releases HID", RuntimeDeviceTests.RestoreFailureStillReleasesHid),
            ("profile switch requires a fresh baseline", RuntimeDeviceTests.ProfileSwitchRequiresFreshBaseline),
            ("malformed lighting replies fail before evidence", RuntimeDeviceTests.MalformedLightingRepliesFailBeforeEvidence),
            ("concurrent different candidates remain atomic", RuntimeDeviceTests.ConcurrentDifferentCandidatesRemainAtomic),
            ("scan and mutation after stop fail closed", RuntimeDeviceTests.ScanAndMutationAfterStopFailClosed),
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"RESULT passed={tests.Length - failures} failed={failures}");
        return failures == 0 ? 0 : 1;
    }
}
