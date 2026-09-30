namespace Vorotex.K15.StatusLab;

internal static class K15LayoutAuthorityRecoveryRunner
{
    // HID APIs are synchronous and may block. Start the full recovery pipeline
    // on a worker so awaits inside cooperative capture cannot return it to the
    // caller's WinForms SynchronizationContext.
    internal static Task<T> RunAsync<T>(Func<Task<T>> recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        return Task.Run(recovery);
    }
}
