namespace Vorotex.K15.Runtime.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("initial snapshot is deterministic and healthy", ContractTests.InitialSnapshotIsDeterministicAndHealthy),
            ("enum wire names are explicit and stable", ContractTests.EnumWireNamesAreExplicitAndStable),
            ("unknown wire values fail closed", ContractTests.UnknownWireValuesFailClosed),
            ("host enforces single instance ownership", RuntimeHostTests.HostEnforcesSingleInstanceOwnership),
            ("host shuts down through cancellation", RuntimeHostTests.HostShutsDownThroughCancellation),
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
