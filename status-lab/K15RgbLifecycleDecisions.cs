namespace Vorotex.K15.StatusLab;

internal static class K15RgbLifecycleDecisions
{
    public static void ParkSnapshots<T>(
        IEnumerable<KeyValuePair<byte, T>> snapshots,
        IDictionary<byte, T> pendingRestores)
    {
        foreach (var pair in snapshots)
            pendingRestores[pair.Key] = pair.Value;
    }

    public static T RestoreBeforeCapture<T>(
        bool hasPendingRestore,
        Action restorePending,
        Func<T> captureFresh)
    {
        if (hasPendingRestore)
            restorePending();
        return captureFresh();
    }

    public static bool RunAfterTeardown(bool teardownSucceeded, Func<bool> operation) =>
        teardownSucceeded && operation();

    public static bool IsReconnectUsable(bool reconnectSucceeded, bool hasCurrentController) =>
        reconnectSucceeded && hasCurrentController;
}
