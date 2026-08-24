using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Vorotex.K15.StatusLab;

internal sealed class WindowsNotificationPoller : IAsyncDisposable
{
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, NotificationSnapshot> _known = new(StringComparer.Ordinal);
    private UserNotificationListener? _listener;
    private Task? _loopTask;
    private bool _primed;

    public event Action<string>? StatusChanged;

    public WindowsNotificationPoller(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public async Task<bool> StartAsync()
    {
        if (_loopTask is not null)
            return true;

        _listener = UserNotificationListener.Current;
        var access = await _listener.RequestAccessAsync();
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "windows_notification",
            @event = "notification_access",
            status = access.ToString()
        });

        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            StatusChanged?.Invoke($"Уведомления: {access}");
            return false;
        }

        StatusChanged?.Invoke("Уведомления: доступ разрешен");
        _loopTask = Task.Run(PollLoopAsync);
        return true;
    }

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EventJournal.Append(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    source = "windows_notification",
                    @event = "notification_poll_error",
                    exception = ex.GetType().FullName,
                    hresult = ex.HResult
                });
                StatusChanged?.Invoke($"Уведомления: ошибка 0x{ex.HResult:X8}");
            }

            try
            {
                await Task.Delay(_pollInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync()
    {
        if (_listener is null)
            return;

        var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        var next = new Dictionary<string, NotificationSnapshot>(StringComparer.Ordinal);

        foreach (var notification in current)
        {
            NotificationSnapshot snapshot;
            try
            {
                snapshot = NotificationSnapshot.From(notification);
            }
            catch
            {
                continue;
            }

            next[snapshot.Key] = snapshot;

            if (!_primed)
            {
                Log("windows_notification_present", snapshot);
            }
            else if (!_known.ContainsKey(snapshot.Key))
            {
                Log("windows_notification_added", snapshot);
            }
        }

        if (_primed)
        {
            foreach (var previous in _known.Values)
            {
                if (!next.ContainsKey(previous.Key))
                    Log("windows_notification_removed", previous);
            }
        }

        _known.Clear();
        foreach (var pair in next)
            _known[pair.Key] = pair.Value;

        _primed = true;
        StatusChanged?.Invoke($"Уведомления: {_known.Count} активных");
    }

    private static void Log(string eventName, NotificationSnapshot snapshot)
    {
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "windows_notification",
            @event = eventName,
            notificationId = snapshot.NotificationId,
            notificationCreatedUtc = snapshot.CreationTime.ToUniversalTime(),
            appName = snapshot.AppName,
            appUserModelId = snapshot.AppUserModelId,
            packageFamilyName = snapshot.PackageFamilyName
        });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    private sealed record NotificationSnapshot(
        string Key,
        uint NotificationId,
        DateTimeOffset CreationTime,
        string AppName,
        string AppUserModelId,
        string PackageFamilyName)
    {
        public static NotificationSnapshot From(UserNotification notification)
        {
            var appInfo = notification.AppInfo;
            var appName = Safe(() => appInfo.DisplayInfo.DisplayName);
            var aumid = Safe(() => appInfo.AppUserModelId);
            var pfn = Safe(() => appInfo.PackageFamilyName);
            var key = $"{aumid}|{pfn}|{notification.Id}";

            return new NotificationSnapshot(
                key,
                notification.Id,
                notification.CreationTime,
                appName,
                aumid,
                pfn);
        }

        private static string Safe(Func<string> getter)
        {
            try
            {
                return getter() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
