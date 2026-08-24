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

var postToolReducer = new StateReducer();
postToolReducer.Apply(Hook(t.AddSeconds(10), "UserPromptSubmit"));
postToolReducer.Apply(Hook(t.AddSeconds(11), "PermissionRequest"));
Require(postToolReducer.State == K15NormalizedState.Waiting, "PermissionRequest must enter WAITING before PostToolUse.");
postToolReducer.Apply(Hook(t.AddSeconds(12), "PostToolUse"));
Require(postToolReducer.State == K15NormalizedState.Running,
    "PostToolUse after an approval must resume RUNNING even when the Windows toast remains.");

var errorHintReducer = new StateReducer();
errorHintReducer.Apply(Hook(t.AddSeconds(14), "UserPromptSubmit"));
errorHintReducer.Apply(Hook(t.AddSeconds(15), "Stop"));
errorHintReducer.Apply(Notification(t.AddSeconds(16), "windows_notification_added", 102, error: true));
Require(errorHintReducer.State == K15NormalizedState.DonePendingAttention,
    "Toast error keywords must not create semantic ERROR without a high-confidence source.");
errorHintReducer.Apply(Notification(t.AddSeconds(17), "windows_notification_removed", 102, error: true));
Require(errorHintReducer.State == K15NormalizedState.Normal,
    "Removing the tracked post-Stop notification must still return to NORMAL.");

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

var earlyToastReducer = new StateReducer();
earlyToastReducer.Apply(Hook(t.AddSeconds(30), "UserPromptSubmit"));
earlyToastReducer.Apply(Notification(t.AddSeconds(31.000), "windows_notification_added", 200));
earlyToastReducer.Apply(Hook(t.AddSeconds(31.100), "PermissionRequest"));
Require(earlyToastReducer.State == K15NormalizedState.Waiting,
    "PermissionRequest must enter WAITING when a toast arrived just before the hook.");
earlyToastReducer.Apply(Notification(t.AddSeconds(33), "windows_notification_removed", 200));
Require(earlyToastReducer.State == K15NormalizedState.Running,
    "A pre-hook correlated permission toast must resolve WAITING when removed.");

var timeoutReducer = new StateReducer();
timeoutReducer.Apply(Hook(t.AddSeconds(40), "UserPromptSubmit"));
timeoutReducer.Apply(Hook(t.AddSeconds(41), "Stop"));
timeoutReducer.Apply(Notification(t.AddSeconds(42), "windows_notification_added", 201));
Require(timeoutReducer.State == K15NormalizedState.DonePendingAttention,
    "Post-Stop notification must keep DONE pending.");
Require(timeoutReducer.Tick(t.AddSeconds(55)) is null,
    "DONE must remain visible before the 15-second timeout.");
var timeoutTransition = timeoutReducer.Tick(t.AddSeconds(56.1));
Require(timeoutTransition is not null &&
        timeoutReducer.State == K15NormalizedState.Normal &&
        timeoutTransition.Reason == "done_attention_timeout",
    "DONE must auto-restore to NORMAL after the attention timeout.");

var report = K15HidProtocol.FrameReport(0x09, 0x12, 0, 0x0064, new byte[] { 1, 2, 3 });
Require(report.Length == 41, "HID report must be 41 bytes.");
Require(report[0] == 0x06 && report[3] == 0x09 && report[4] == 0x12, "HID report header mismatch.");
Require(report[6] == 0x64 && report[7] == 0x00 && report[8] == 3, "HID report address/length mismatch.");

var originalHeader = Enumerable.Range(0, 25).Select(value => (byte)value).ToArray();
var runningHeader = K15HidProtocol.CreateRunningHeader(originalHeader);
Require(runningHeader[0] == K15HidProtocol.TetrisMode,
    "RUNNING must select the hardware Tetris/Enraptured mode.");
Require(runningHeader.Skip(1).SequenceEqual(originalHeader.Skip(1)),
    "RUNNING must preserve non-mode lighting header bytes.");

var waitingRecord = K15HidProtocol.CreateAlertLightingRecord(K15NormalizedState.Waiting, speed: 6);
Require(waitingRecord.Length == 25, "Lighting detail must be 25 bytes.");
Require(waitingRecord[3] == 0x01, "Single-color breathing should enable one palette slot.");
Require(waitingRecord[4] == 0xFF && waitingRecord[5] == 0xFF && waitingRecord[6] == 0xFF,
    "WAITING must encode white in G,R,B wire order.");

var doneRecord = K15HidProtocol.CreateAlertLightingRecord(K15NormalizedState.DonePendingAttention, speed: 3);
Require(doneRecord[4] == 0xFF && doneRecord[5] == 0x00 && doneRecord[6] == 0x00,
    "DONE must encode green in G,R,B wire order.");

var profileA = K15HidProtocol.CreateProfileFlashLightingRecord(0);
Require(profileA[4] == 0x00 && profileA[5] == 0xFF && profileA[6] == 0x00,
    "Profile A flash must encode red in G,R,B wire order.");
var profileB = K15HidProtocol.CreateProfileFlashLightingRecord(1);
Require(profileB[4] == 0x00 && profileB[5] == 0x00 && profileB[6] == 0xFF,
    "Profile B flash must encode blue in G,R,B wire order.");

var alertHeader = K15HidProtocol.CreateAlertHeader(originalHeader);
Require(alertHeader[0] == K15HidProtocol.SingleColorBreathingMode,
    "Alert header must select single-color breathing.");
Require(alertHeader.Skip(1).SequenceEqual(originalHeader.Skip(1)),
    "Alert header must preserve non-mode bytes.");

var constantHeader = K15HidProtocol.CreateConstantHeader(alertHeader);
Require(constantHeader[0] == K15HidProtocol.ConstantMode,
    "Stale notifier residue must be repairable back to Constant mode.");
Require(constantHeader.Skip(1).SequenceEqual(alertHeader.Skip(1)),
    "Constant baseline repair must preserve non-mode header bytes.");

Require(K15HidProtocol.IsSupportedDevice(0xB6A4, 0x4100), "Physical K15 VID/PID must be accepted.");
Require(!K15HidProtocol.IsSupportedDevice(0x1234, 0x4100), "Unrelated VID must be rejected.");

Console.WriteLine("State reducer + HID protocol smoke tests: PASS");
