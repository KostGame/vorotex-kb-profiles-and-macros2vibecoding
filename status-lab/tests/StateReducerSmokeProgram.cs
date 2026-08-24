using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static StatusInputEvent Hook(DateTimeOffset t, string name) =>
    new(t, "codex_hook", name);

static StatusInputEvent Notification(DateTimeOffset t, string name, uint id, bool error = false) =>
    new(t, "windows_notification", name, id, "OpenAI.Codex_test", error);

var t = DateTimeOffset.Parse("2026-08-24T17:30:00Z");
var reducer = new StateReducer();

Require(reducer.State == K15NormalizedState.Normal, "Initial state must be NORMAL.");

reducer.Apply(Hook(t, "UserPromptSubmit"));
Require(reducer.State == K15NormalizedState.Running, "UserPromptSubmit must enter RUNNING.");

reducer.Apply(Hook(t.AddSeconds(1), "PermissionRequest"));
Require(reducer.State == K15NormalizedState.Waiting, "PermissionRequest must enter WAITING.");

reducer.Apply(Hook(t.AddSeconds(1.2), "PermissionRequest"));
Require(reducer.State == K15NormalizedState.Waiting, "Repeated PermissionRequest must stay WAITING.");

reducer.Apply(Notification(t.AddSeconds(2), "windows_notification_added", 100));
Require(reducer.State == K15NormalizedState.Waiting, "Tracked permission notification must not change WAITING.");

reducer.Apply(Notification(t.AddSeconds(4), "windows_notification_removed", 100));
Require(reducer.State == K15NormalizedState.Running, "Resolved permission notification must return to RUNNING.");

reducer.Apply(Hook(t.AddSeconds(5), "Stop"));
Require(reducer.State == K15NormalizedState.DonePendingAttention, "Stop must enter DONE_PENDING_ATTENTION.");

reducer.Apply(Notification(t.AddSeconds(6), "windows_notification_added", 101));
Require(reducer.State == K15NormalizedState.DonePendingAttention, "Post-Stop completion notification keeps DONE.");

reducer.Apply(Notification(t.AddSeconds(8), "windows_notification_removed", 101));
Require(reducer.State == K15NormalizedState.Normal, "Removing tracked completion notification must acknowledge DONE.");

reducer.Apply(Hook(t.AddSeconds(10), "UserPromptSubmit"));
reducer.Apply(Hook(t.AddSeconds(11), "Stop"));
reducer.Apply(Notification(t.AddSeconds(12), "windows_notification_added", 102, error: true));
Require(reducer.State == K15NormalizedState.Error, "Post-Stop error hint must enter ERROR.");

reducer.Apply(Notification(t.AddSeconds(13), "windows_notification_removed", 102, error: true));
Require(reducer.State == K15NormalizedState.Normal, "Removing tracked error notification must return to NORMAL.");

reducer.Apply(Hook(t.AddSeconds(20), "UserPromptSubmit"));
reducer.Apply(new StatusInputEvent(
    t.AddSeconds(21),
    "windows_notification",
    "windows_notification_added",
    103,
    "Microsoft.OtherApp_test"));
Require(reducer.State == K15NormalizedState.Running, "Unrelated notifications must not affect state.");

reducer.Apply(Hook(t.AddSeconds(22), "SessionEnd"));
Require(reducer.State == K15NormalizedState.Normal, "SessionEnd must return to NORMAL.");

Console.WriteLine("State reducer smoke tests: PASS");
