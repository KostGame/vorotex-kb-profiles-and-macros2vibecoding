namespace Vorotex.K15.StatusLab;

internal enum WindowsNotificationChangeKind
{
    Present,
    Added,
    Updated,
    Removed
}

internal sealed record WindowsNotificationObservation(
    WindowsNotificationChangeKind ChangeKind,
    string Key,
    uint NotificationId,
    DateTimeOffset CreationTime,
    string AppName,
    string AppUserModelId,
    string PackageFamilyName,
    string TextFingerprint,
    string Title,
    string Body,
    int TextElementCount,
    int[] TextLengths,
    string TextClass,
    bool PermissionHint,
    bool CompletionHint,
    bool ErrorHint);
