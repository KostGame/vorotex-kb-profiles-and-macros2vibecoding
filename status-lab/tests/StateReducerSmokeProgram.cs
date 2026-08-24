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

var config = StatusLabConfig.CreateDefault();
Require(config.WireColorOrder == WireColorOrder.RGB,
    "Physical K15 default must use RGB order after owner canary calibration.");
Require(config.ActivationSignal.Mode == K15LightingMode.FlowingWater &&
        config.ActivationSignal.Brightness == 4 &&
        config.ActivationSignal.Speed == 7 &&
        config.ActivationSignal.Colors.SequenceEqual(new[] { "red", "blue" }),
    "Activation signal must default to fast red+blue Flowing Water at brightness 4.");
Require(config.States.Running.Mode == K15LightingMode.TetrisBlocks,
    "RUNNING must default to Tetris blocks.");
Require(config.Profiles.A.Normal.Mode == K15LightingMode.Constant &&
        config.Profiles.A.Normal.Colors.SequenceEqual(new[] { "red" }),
    "Profile A configured normal must be constant red.");
Require(config.Profiles.B.Normal.Mode == K15LightingMode.Constant &&
        config.Profiles.B.Normal.Colors.SequenceEqual(new[] { "blue" }),
    "Profile B configured normal must be constant blue.");

Require(K15HidProtocol.ModeCode(K15LightingMode.FlowingWater) == 0x82,
    "Flowing Water must map to native mode 0x82.");
Require(K15HidProtocol.ModeCode(K15LightingMode.TetrisBlocks) == 0x86,
    "Tetris blocks must map to native mode 0x86.");
Require(K15HidProtocol.ModeRecordAddress(K15LightingMode.FlowingWater) == 50,
    "Flowing Water detail record must use address 2*25.");
Require(K15HidProtocol.ModeRecordAddress(K15LightingMode.TetrisBlocks) == 150,
    "Tetris detail record must use address 6*25.");

var activationRecord = K15HidProtocol.CreateEffectRecord(config.ActivationSignal, WireColorOrder.RGB);
Require(activationRecord[0] == 7 && activationRecord[2] == 2,
    "Activation speed/brightness encoding mismatch.");
Require(activationRecord[3] == 0x03,
    "Two activation colors must enable the first two palette slots.");
Require(activationRecord[4] == 0xFF && activationRecord[5] == 0x00 && activationRecord[6] == 0x00,
    "Physical red must be encoded as R,G,B after owner calibration.");
Require(activationRecord[7] == 0x00 && activationRecord[8] == 0x00 && activationRecord[9] == 0xFF,
    "Physical blue must be the second activation palette color.");

var legacyOrderRecord = K15HidProtocol.CreateEffectRecord(config.Profiles.A.SwitchSignal, WireColorOrder.GRB);
Require(legacyOrderRecord[4] == 0x00 && legacyOrderRecord[5] == 0xFF,
    "GRB compatibility option must remain explicitly available.");

var originalHeader = Enumerable.Range(0, 25).Select(value => (byte)value).ToArray();
var runningHeader = K15HidProtocol.CreateEffectHeader(originalHeader, config.States.Running);
Require(runningHeader[0] == K15HidProtocol.TetrisMode,
    "Configured RUNNING effect must select Tetris/Enraptured mode.");
Require(runningHeader.Skip(1).SequenceEqual(originalHeader.Skip(1)),
    "Configured effect header must preserve non-mode bytes.");

Require(K15HidProtocol.IsSupportedDevice(0xB6A4, 0x4100), "Physical K15 VID/PID must be accepted.");
Require(!K15HidProtocol.IsSupportedDevice(0x1234, 0x4100), "Unrelated VID must be rejected.");

Console.WriteLine("State reducer + editable RGB config + HID protocol smoke tests: PASS");
