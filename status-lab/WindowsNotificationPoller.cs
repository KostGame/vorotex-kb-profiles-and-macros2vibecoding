using System.Security.Cryptography;
using System.Text;
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
            packageFamilyName = snapshot.PackageFamilyName,
            textFingerprint = snapshot.TextFingerprint,
            textElementCount = snapshot.TextElementCount,
            textLengths = snapshot.TextLengths,
            textClass = snapshot.TextClass,
            permissionHint = snapshot.PermissionHint,
            completionHint = snapshot.CompletionHint,
            errorHint = snapshot.ErrorHint
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
        string PackageFamilyName,
        string TextFingerprint,
        int TextElementCount,
        int[] TextLengths,
        string TextClass,
        bool PermissionHint,
        bool CompletionHint,
        bool ErrorHint)
    {
        public static NotificationSnapshot From(UserNotification notification)
        {
            var appInfo = notification.AppInfo;
            var appName = Safe(() => appInfo.DisplayInfo.DisplayName);
            var aumid = Safe(() => appInfo.AppUserModelId);
            var pfn = Safe(() => appInfo.PackageFamilyName);
            var key = $"{aumid}|{pfn}|{notification.Id}";
            var text = ReadTextMetadata(notification);

            return new NotificationSnapshot(
                key,
                notification.Id,
                notification.CreationTime,
                appName,
                aumid,
                pfn,
                text.Fingerprint,
                text.ElementCount,
                text.Lengths,
                text.Classification,
                text.PermissionHint,
                text.CompletionHint,
                text.ErrorHint);
        }

        private static NotificationTextMetadata ReadTextMetadata(UserNotification notification)
        {
            try
            {
                var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding is null)
                    return NotificationTextMetadata.Empty;

                var elements = binding.GetTextElements()
                    .Select(element => element.Text ?? string.Empty)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();

                if (elements.Length == 0)
                    return NotificationTextMetadata.Empty;

                var normalized = string.Join("\n", elements)
                    .Trim()
                    .ToLowerInvariant();

                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
                var fingerprint = Convert.ToHexString(bytes).ToLowerInvariant();

                var permission = ContainsAny(normalized,
                    "permission", "approve", "approval", "allow", "confirm", "waiting",
                    "needs your", "requires attention", "input",
                    "разреш", "подтверд", "ожида", "ввод", "требует");
                var completion = ContainsAny(normalized,
                    "done", "complete", "completed", "finished", "ready",
                    "готов", "заверш");
                var error = ContainsAny(normalized,
                    "error", "failed", "failure",
                    "ошиб", "не удалось");

                var classification = error
                    ? "error_hint"
                    : permission
                        ? "permission_hint"
                        : completion
                            ? "completion_hint"
                            : "generic";

                return new NotificationTextMetadata(
                    fingerprint,
                    elements.Length,
                    elements.Select(value => value.Length).ToArray(),
                    classification,
                    permission,
                    completion,
                    error);
            }
            catch
            {
                return NotificationTextMetadata.Empty;
            }
        }

        private static bool ContainsAny(string value, params string[] needles) =>
            needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

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

    private sealed record NotificationTextMetadata(
        string Fingerprint,
        int ElementCount,
        int[] Lengths,
        string Classification,
        bool PermissionHint,
        bool CompletionHint,
        bool ErrorHint)
    {
        public static NotificationTextMetadata Empty { get; } =
            new(string.Empty, 0, Array.Empty<int>(), "unknown", false, false, false);
    }
}
