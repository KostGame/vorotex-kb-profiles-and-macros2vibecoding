namespace Vorotex.K15.StatusLab;

// The authority session uses this same lock/version primitive for snapshots,
// invalidation, compare-and-publish, and dispatch authorization.
internal sealed class K15LayoutAuthorityVersionGuard
{
    private readonly object _sync = new();
    private long _version;

    internal T Capture<T>(Func<long, T> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_sync) return capture(_version);
    }

    internal bool TryPublish(long capturedVersion, Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_sync)
        {
            if (capturedVersion != _version) return false;
            publish();
            _version++;
            return true;
        }
    }

    internal long Invalidate(Action clear)
    {
        ArgumentNullException.ThrowIfNull(clear);
        lock (_sync)
        {
            clear();
            return ++_version;
        }
    }

    internal bool TryInvalidate(long capturedVersion, Action clear, out long invalidatedVersion)
    {
        ArgumentNullException.ThrowIfNull(clear);
        lock (_sync)
        {
            if (capturedVersion != _version)
            {
                invalidatedVersion = _version;
                return false;
            }
            clear();
            invalidatedVersion = ++_version;
            return true;
        }
    }

    internal bool ExecuteIfCurrent(long capturedVersion, Func<bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync) return capturedVersion == _version && action();
    }

    internal void Execute(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync) action();
    }
}
