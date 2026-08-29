using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static StatusInputEvent Hook(DateTimeOffset t, string name, string session = "session-main", string cwd = @"C:\work\main", string turn = "") =>
    new(t, "codex_hook", name, SessionId: session, TurnId: turn, Cwd: cwd);
static StatusInputEvent Notification(DateTimeOffset t, string name, uint id, bool error = false) =>
    new(t, "windows_notification", name, id, "OpenAI.Codex_test", error);
static StatusInputEvent Approval(DateTimeOffset t, string decision, string rpcId = "1", string threadId = "", string turnId = "") =>
    new(t, "codex_stdio_bridge", "approval_resolved", SchemaVersion: "k15-codex-approval/v1",
        Decision: decision, RpcIdType: "number", RpcId: rpcId, ThreadId: threadId, TurnId: turnId, ItemId: "item-1");

var t = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
var reducer = new StateReducer();
Require(reducer.State == K15NormalizedState.Normal, "Initial state must be NORMAL.");
reducer.Apply(Hook(t, "UserPromptSubmit"));
Require(reducer.State == K15NormalizedState.Running, "UserPromptSubmit must enter RUNNING.");
Require(reducer.FocusedSessionId == "session-main", "Main task session must become focused.");
reducer.Apply(Hook(t.AddSeconds(1), "PermissionRequest"));
Require(reducer.State == K15NormalizedState.Waiting, "PermissionRequest must enter WAITING.");
var resolved = reducer.Apply(Approval(t.AddSeconds(1), "accept", turnId: "turn-approval"));
Require(resolved is null && reducer.State == K15NormalizedState.Waiting,
    "An approval without exact turn/thread correlation must not infer RUNNING.");
var exactApproval = new StateReducer();
exactApproval.Apply(Hook(t, "UserPromptSubmit", turn: "turn-approval"));
exactApproval.Apply(Hook(t.AddSeconds(1), "PermissionRequest", turn: "turn-approval"));
var bridgeTransition = exactApproval.Apply(Approval(t.AddSeconds(2), "accept", turnId: "turn-approval"));
Require(bridgeTransition?.Current == K15NormalizedState.Running &&
        bridgeTransition.Reason == "codex_approval_resolved" && exactApproval.State == K15NormalizedState.Running,
    "Exact sanitized accept must resume the matching WAITING session immediately.");

var sessionDecision = new StateReducer();
sessionDecision.Apply(Hook(t, "UserPromptSubmit", turn: "turn-decision"));
sessionDecision.Apply(Hook(t.AddSeconds(1), "PermissionRequest", turn: "turn-decision"));
sessionDecision.Apply(Approval(t.AddSeconds(2), "decline", rpcId: "2", turnId: "turn-decision"));
Require(sessionDecision.State == K15NormalizedState.Waiting,
    "decline must remain distinct and must not map to RUNNING.");
sessionDecision.Apply(Approval(t.AddSeconds(3), "cancel", rpcId: "3", turnId: "turn-decision"));
Require(sessionDecision.State == K15NormalizedState.Waiting,
    "cancel must remain distinct and must not map to RUNNING.");
sessionDecision.Apply(Approval(t.AddSeconds(4), "acceptForSession", rpcId: "4", turnId: "turn-decision"));
Require(sessionDecision.State == K15NormalizedState.Running,
    "acceptForSession must resume the exact waiting session.");

var parallelApprovals = new StateReducer();
parallelApprovals.Apply(Hook(t, "UserPromptSubmit", "session-a", turn: "turn-a"));
parallelApprovals.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "session-a", turn: "turn-a"));
parallelApprovals.Apply(Hook(t.AddSeconds(2), "UserPromptSubmit", "session-b", turn: "turn-b"));
parallelApprovals.Apply(Hook(t.AddSeconds(3), "PermissionRequest", "session-b", turn: "turn-b"));
parallelApprovals.Apply(Approval(t.AddSeconds(4), "accept", rpcId: "5", turnId: "turn-b"));
Require(parallelApprovals.Snapshot.ApprovalWaitingCount == 1 && parallelApprovals.Snapshot.RunningCount == 1,
    "Parallel approvals must resolve only the matching turn.");
Require(parallelApprovals.Apply(Approval(t.AddSeconds(5), "accept", rpcId: "6", turnId: "turn-a"))?.Current == K15NormalizedState.Running,
    "The second parallel approval must resolve independently.");
Require(parallelApprovals.Apply(new StatusInputEvent(t.AddSeconds(6), "codex_stdio_bridge", "serverRequest/resolved",
        SchemaVersion: "k15-codex-approval/v1", Decision: "accept", RpcIdType: "number", RpcId: "7", TurnId: "turn-a")) is null,
    "Generic serverRequest/resolved must never map to RUNNING.");
var parsedApproval = JournalStateNormalizer.ParseInput("""
{"schemaVersion":"k15-codex-approval/v1","timestampUtc":"2026-08-25T00:00:07Z","source":"codex_stdio_bridge","event":"approval_resolved","decision":"accept","rpcIdType":"number","rpcId":"8","threadId":"session-a","turnId":"turn-a","itemId":"item-a"}
""");
Require(parsedApproval?.Decision == "accept" && parsedApproval.RpcId == "8" && parsedApproval.RpcIdType == "number" && parsedApproval.TurnId == "turn-a",
    "Status Lab must accept only the versioned sanitized approval schema.");
Require(JournalStateNormalizer.ParseInput("""
{"schemaVersion":"k15-codex-approval/v1","timestampUtc":"2026-08-25T00:00:08Z","source":"codex_stdio_bridge","event":"approval_resolved","decision":"accept","rpcIdType":"number","rpcId":"9","turnId":"turn-a","command":"MUST NOT BE ACCEPTED"}
""") is null,
    "Approval parser must reject arbitrary payload fields.");
Require(JournalStateNormalizer.ParseInput("""
{"schemaVersion":"k15-codex-approval/v1","timestampUtc":"2026-08-25T00:00:09Z","source":"codex_stdio_bridge","event":"approval_resolved","decision":"accept","rpcIdType":"number","rpcId":"10","turnId":42}
""") is null,
    "Approval parser must reject non-string correlation fields.");
var approvalTransition = reducer.Apply(Hook(t.AddSeconds(2), "PreToolUse"));
Require(reducer.State == K15NormalizedState.Running, "PreToolUse must resume RUNNING immediately after in-Codex approval.");
Require(approvalTransition?.Reason == "codex_pre_tool_use", "PreToolUse approval transition reason changed.");
reducer.Apply(Hook(t.AddSeconds(3), "PermissionRequest"));
Require(reducer.State == K15NormalizedState.Waiting, "Second PermissionRequest must enter WAITING.");
reducer.Apply(Hook(t.AddSeconds(4), "PostToolUse"));
Require(reducer.State == K15NormalizedState.Running, "PostToolUse remains a fallback RUNNING confirmation.");
reducer.Apply(Hook(t.AddSeconds(5), "Stop"));
Require(reducer.State == K15NormalizedState.DonePendingAttention, "Stop must enter DONE.");
reducer.Apply(Notification(t.AddSeconds(6), "windows_notification_added", 101, error: true));
Require(reducer.State == K15NormalizedState.DonePendingAttention, "Toast keywords must not create semantic ERROR.");
reducer.Apply(Notification(t.AddSeconds(7), "windows_notification_removed", 101));
Require(reducer.State == K15NormalizedState.DonePendingAttention,
    "Removing a completion toast must never acknowledge DONE_UNREAD.");

var manual = new StateReducer();
manual.Apply(Hook(t, "UserPromptSubmit", "manual-main"));
manual.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "manual-main"));
Require(manual.State == K15NormalizedState.Waiting, "Manual-reset setup must be WAITING.");
var manualTransition = manual.Acknowledge(t.AddSeconds(2));
Require(manualTransition?.Current == K15NormalizedState.Normal && manual.State == K15NormalizedState.Normal,
    "Manual tray reset must clear WAITING/DONE to NORMAL.");

var preStop = new StateReducer(30);
preStop.Apply(Hook(t, "UserPromptSubmit", "pre-stop"));
preStop.Apply(Notification(t.AddSeconds(5), "windows_notification_added", 202));
preStop.Apply(Hook(t.AddSeconds(5.1), "Stop", "pre-stop"));
Require(preStop.State == K15NormalizedState.DonePendingAttention,
    "Completion toast arriving immediately before Stop must still correlate to DONE.");
preStop.Apply(Notification(t.AddSeconds(8), "windows_notification_removed", 202));
Require(preStop.State == K15NormalizedState.DonePendingAttention,
    "Removing a pre-Stop completion toast must not resolve DONE_UNREAD.");

var timeout = new StateReducer(30);
timeout.Apply(Hook(t, "UserPromptSubmit", "timeout-main"));
timeout.Apply(Hook(t.AddSeconds(1), "Stop", "timeout-main"));
Require(timeout.Tick(t.AddSeconds(30.9)) is null, "DONE fallback must not fire before 30 seconds from Stop.");
var timeoutTransition = timeout.Tick(t.AddSeconds(31.1));
Require(timeoutTransition?.Current == K15NormalizedState.Normal && timeoutTransition.Reason == "stale_attention_timeout",
    "Configured stale reset must use an explicit stale_attention_timeout reason.");

var parallel = new StateReducer();
parallel.Apply(Hook(t, "UserPromptSubmit", "main-A", @"D:\AI_AGENT_PROJECTS\agentloop-exchange-manual-win-001"));
Require(parallel.State == K15NormalizedState.Running, "Main A must be RUNNING.");
parallel.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "memory-B", @"C:\Users\Desktop\.codex-agentloop\memories"));
parallel.Apply(Hook(t.AddSeconds(2), "SessionEnd", "memory-B", @"C:\Users\Desktop\.codex-agentloop\memories"));
Require(parallel.State == K15NormalizedState.Running,
    "Background memories SessionEnd must not reset the foreground main session.");
Require(parallel.FocusedSessionId == "main-A", "Internal memories session must never steal focus.");

parallel.Apply(Hook(t.AddSeconds(3), "UserPromptSubmit", "main-C", @"D:\AI_AGENT_PROJECTS\other-task"));
Require(parallel.FocusedSessionId == "main-C", "Newest real task activity must take focus.");
parallel.Apply(Hook(t.AddSeconds(4), "SessionEnd", "main-C", @"D:\AI_AGENT_PROJECTS\other-task"));
Require(parallel.State == K15NormalizedState.Running && parallel.FocusedSessionId == "main-A",
    "Ending focused session C must fall back to still-active session A.");

var rehydrated = new StateReducer();
rehydrated.Rehydrate(new[]
{
    Hook(t, "UserPromptSubmit", "rehydrate-main", @"D:\AI_AGENT_PROJECTS\rehydrate"),
    Hook(t.AddSeconds(1), "PermissionRequest", "rehydrate-main", @"D:\AI_AGENT_PROJECTS\rehydrate"),
    Hook(t.AddSeconds(2), "PreToolUse", "rehydrate-main", @"D:\AI_AGENT_PROJECTS\rehydrate"),
    Hook(t.AddSeconds(3), "UserPromptSubmit", "rehydrate-memory", @"C:\Users\Desktop\.codex-agentloop\memories"),
    Hook(t.AddSeconds(4), "SessionEnd", "rehydrate-memory", @"C:\Users\Desktop\.codex-agentloop\memories")
});
Require(rehydrated.State == K15NormalizedState.Running,
    "Startup replay must recover a still-running main Codex session after approval.");
Require(rehydrated.FocusedSessionId == "rehydrate-main", "Rehydrate must recover foreground main session focus.");
Require(StateReducer.IsInternalCwd(@"C:\Users\Desktop\.codex-agentloop\memories"),
    "AgentLoop memories cwd must be classified internal.");
Require(!StateReducer.IsInternalCwd(@"D:\AI_AGENT_PROJECTS\task"), "Normal project cwd must not be internal.");

var ledger = new StateReducer(30);
ledger.Apply(Hook(t, "UserPromptSubmit", "A"));
ledger.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "B"));
ledger.Apply(Hook(t.AddSeconds(2), "Stop", "B"));
ledger.Apply(Hook(t.AddSeconds(3), "UserPromptSubmit", "C"));
ledger.Apply(Hook(t.AddSeconds(4), "PermissionRequest", "C"));
var snapshot = ledger.Snapshot;
Require(snapshot.RunningCount == 1 && snapshot.DoneUnreadCount == 1 && snapshot.ApprovalWaitingCount == 1 &&
        snapshot.AggregateState == K15NormalizedState.Waiting,
    "Scenario A: WAITING must outrank DONE and RUNNING across sessions.");
ledger.Apply(Hook(t.AddSeconds(5), "PreToolUse", "C"));
snapshot = ledger.Snapshot;
Require(snapshot.ApprovalWaitingCount == 0 && snapshot.DoneUnreadCount == 1 && snapshot.RunningCount == 2 &&
        snapshot.AggregateState == K15NormalizedState.DonePendingAttention,
    "Scenario A: resolved approval must reveal remaining DONE_UNREAD.");
ledger.Apply(Hook(t.AddSeconds(6), "UserPromptSubmit", "B"));
Require(ledger.Snapshot.DoneUnreadCount == 0 && ledger.State == K15NormalizedState.Running,
    "Scenario A: same-session UserPromptSubmit must acknowledge its old DONE.");

var unrelated = new StateReducer();
unrelated.Apply(Hook(t, "Stop", "A"));
unrelated.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "B"));
Require(unrelated.Snapshot.DoneUnreadCount == 1 && unrelated.State == K15NormalizedState.DonePendingAttention,
    "Scenarios B/D: RUNNING or a prompt in another session must not clear DONE_UNREAD.");

var priority = new StateReducer();
for (var i = 0; i < 10; i++) priority.Apply(Hook(t.AddSeconds(i), "UserPromptSubmit", $"run-{i}"));
for (var i = 0; i < 3; i++) priority.Apply(Hook(t.AddSeconds(11 + i), "Stop", $"done-{i}"));
priority.Apply(Hook(t.AddSeconds(20), "PermissionRequest", "approval"));
Require(priority.State == K15NormalizedState.Waiting, "Scenario C: approval must outrank all other attention.");

var blockedTimer = new StateReducer(30);
blockedTimer.Apply(Hook(t, "Stop", "done"));
blockedTimer.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "running"));
Require(blockedTimer.Tick(t.AddSeconds(40)) is null && blockedTimer.Snapshot.DoneUnreadCount == 1,
    "Scenario F: stale reset must be blocked while any session RUNS.");
blockedTimer.Apply(Hook(t.AddSeconds(41), "Stop", "running"));
Require(blockedTimer.Tick(t.AddSeconds(70)) is null && blockedTimer.Snapshot.DoneUnreadCount == 2,
    "Scenario H: a new zero-running interval must start a fresh timer.");
Require(blockedTimer.Tick(t.AddSeconds(72))?.Reason == "stale_attention_timeout" && blockedTimer.State == K15NormalizedState.Normal,
    "Scenarios G/H: stale reset must occur only after the fresh idle interval.");

var disabledTimer = new StateReducer(0);
disabledTimer.Apply(Hook(t, "Stop", "done"));
Require(disabledTimer.Tick(t.AddDays(2)) is null && disabledTimer.State == K15NormalizedState.DonePendingAttention,
    "Scenario J: zero must disable automatic stale reset.");

var ended = new StateReducer();
ended.Apply(Hook(t, "Stop", "A"));
ended.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "C"));
ended.Apply(Hook(t.AddSeconds(2), "SessionEnd", "C"));
Require(ended.Snapshot.DoneUnreadCount == 1 && ended.State == K15NormalizedState.DonePendingAttention,
    "Scenario K: SessionEnd must not erase other-session attention.");

var replayLedger = new StateReducer();
replayLedger.Rehydrate(new[]
{
    Hook(t, "UserPromptSubmit", "A"),
    Hook(t.AddSeconds(1), "UserPromptSubmit", "B"),
    Hook(t.AddSeconds(2), "Stop", "B"),
    Hook(t.AddSeconds(3), "UserPromptSubmit", "C"),
    Hook(t.AddSeconds(4), "PermissionRequest", "C")
});
Require(replayLedger.Snapshot.RunningCount == 1 && replayLedger.Snapshot.DoneUnreadCount == 1 &&
        replayLedger.Snapshot.ApprovalWaitingCount == 1 && replayLedger.State == K15NormalizedState.Waiting,
    "Scenario L: journal replay must restore multi-session aggregate attention.");

var config = StatusLabConfig.CreateDefault();
config.Validate();
Require(config.SchemaVersion == 5, "Canonical TOML schema must be v5.");
Require(config.WireColorOrder == WireColorOrder.RGB, "Physical K15 default must use RGB.");
Require(config.DoneAttentionTimeoutSeconds == 30, "DONE fallback timeout default must be 30 seconds.");
Require(config.StaleAttentionTimeoutSeconds == 18000, "Stale attention timeout default must be five hours.");
Require(config.Profiles.A.Color == "#FF0000" && config.Profiles.B.Color == "#0000FF", "Profile identity colors changed.");
Require(config.States.Running.Mode == K15LightingMode.FlowingWater, "RUNNING default must use Flowing Water.");
Require(config.States.Running.Palette == PaletteSource.Profile, "RUNNING must use active profile color.");
Require(config.States.Waiting.Mode == K15LightingMode.SingleColorBreathing && config.States.Waiting.Speed == 7,
    "WAITING must use fast single-color breathing speed 7.");
Require(config.States.Done.Mode == K15LightingMode.SingleColorBreathing && config.States.Done.Speed == 5,
    "DONE must use slower single-color breathing speed 5.");
Require(config.StopSignal.Mode == K15LightingMode.CycleBreathing && config.StopSignal.Palette == PaletteSource.ProfilePair,
    "STOP signal must use two-color Cycle breathing.");
Require(config.ActivationSignal.Enabled && config.ActivationSignal.Mode == K15LightingMode.CycleBreathing &&
        config.ActivationSignal.Palette == PaletteSource.ProfilePair && config.ActivationSignal.Speed == 7,
    "RGB activation must use fast two-color Cycle breathing.");
Require(!config.ProfileSwitch.Enabled && config.ProfileSwitch.DurationSeconds == 0,
    "RC1 must not compete with the keyboard-native profile switch animation.");
Require(StatusLabConfig.IsControlledPaletteMode(K15LightingMode.CycleBreathing),
    "Physically accepted Cycle breathing must be notifier-safe.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.MonoWater), "Native 0x83/Horse race must remain research-only.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.TetrisBlocks), "Tetris must remain research-only for now.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.Neon), "Neon must remain research-only.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.Ambilight), "Ambilight must remain research-only.");

var runningA = config.RenderForProfile(0, config.States.Running);
var runningB = config.RenderForProfile(1, config.States.Running);
var stop = config.RenderForProfile(1, config.StopSignal);
Require(runningA.Colors.SequenceEqual(new[] { "#FF0000" }), "Profile A state renderer must use red only.");
Require(runningB.Colors.SequenceEqual(new[] { "#0000FF" }), "Profile B state renderer must use blue only.");
Require(stop.Colors.SequenceEqual(new[] { "#FF0000", "#0000FF" }),
    "profile_pair renderer must use canonical A then B colors.");

var pairRecord = K15HidProtocol.CreateEffectRecord(stop, WireColorOrder.RGB);
Require(pairRecord[3] == 0b00000011, "Two-color profile_pair must set palette mask 0x03.");
Require(pairRecord[4] == 0xFF && pairRecord[5] == 0 && pairRecord[6] == 0,
    "profile_pair slot 1 must encode profile A red.");
Require(pairRecord[7] == 0 && pairRecord[8] == 0 && pairRecord[9] == 0xFF,
    "profile_pair slot 2 must encode profile B blue.");

var toml = ConfigToml.Serialize(config);
Require(toml.Contains("schema_version = 5", StringComparison.Ordinal), "Canonical TOML must use schema v5.");
Require(toml.Contains("[behavior]", StringComparison.Ordinal) &&
        toml.Contains("stale_attention_timeout_seconds = 18000", StringComparison.Ordinal),
    "Canonical TOML must expose five-hour stale attention behavior.");
Require(toml.Contains("[stop_signal]", StringComparison.Ordinal), "Canonical TOML must include STOP overlay section.");
Require(toml.Contains("palette = \"profile_pair\"", StringComparison.Ordinal),
    "Canonical TOML must expose profile_pair palette source.");
Require(toml.Contains("effect = \"cycle_breathing\"", StringComparison.Ordinal),
    "Canonical TOML must expose physically accepted Cycle breathing.");
Require(toml.Contains("НИКОГДА программно не переключает", StringComparison.Ordinal),
    "Canonical TOML must document observe-only profile policy.");
Require(!toml.Contains("[states.running]\ncolor", StringComparison.Ordinal), "State TOML must not own colors.");
var roundTrip = ConfigToml.Parse(toml);
Require(roundTrip.StopSignal.Palette == PaletteSource.ProfilePair, "TOML round-trip lost STOP palette source.");
Require(roundTrip.StaleAttentionTimeoutSeconds == 18000, "TOML round-trip lost stale timeout.");

var existingV3WithoutBehavior = ConfigToml.Parse("schema_version = 3\n[states.done]\neffect = \"single_color_breathing\"\npalette = \"profile\"\n");
Require(existingV3WithoutBehavior.SchemaVersion == 5 && existingV3WithoutBehavior.StaleAttentionTimeoutSeconds == 18000,
    "Existing schema-v3 config without [behavior] must inherit the safe five-hour stale default.");

var oldV3 = ConfigToml.Parse("""
schema_version = 3
[behavior]
done_attention_timeout_seconds = 15
[profile_switch]
enabled = true
effect = "flowing_water"
palette = "profile"
brightness = 5
speed = 5
direction = 0
duration_seconds = 4
[activation]
enabled = true
effect = "flowing_water"
palette = "profile_pair"
brightness = 5
speed = 5
direction = 0
duration_seconds = 3
""");
Require(oldV3.SchemaVersion == 5 && oldV3.StaleAttentionTimeoutSeconds == 18000,
    "Legacy DONE timeout must not be silently reinterpreted as stale attention.");
Require(!oldV3.ProfileSwitch.Enabled && oldV3.ProfileSwitch.DurationSeconds == 0,
    "Exact beta profile-switch default must migrate to OFF in memory.");
Require(oldV3.ActivationSignal.Mode == K15LightingMode.CycleBreathing && oldV3.ActivationSignal.Speed == 7,
    "Exact beta activation default must migrate to fast Cycle breathing.");

var customV3 = ConfigToml.Parse("""
schema_version = 3
[behavior]
done_attention_timeout_seconds = 45
[profile_switch]
enabled = true
effect = "flowing_water"
palette = "profile"
brightness = 4
speed = 3
direction = 1
duration_seconds = 6
""");
Require(customV3.DoneAttentionTimeoutSeconds == 45 && customV3.StaleAttentionTimeoutSeconds == 18000 &&
        customV3.ProfileSwitch.Enabled && customV3.ProfileSwitch.DurationSeconds == 6,
    "Schema migration must preserve legacy data without reusing it as stale attention.");

var legacyV2 = ConfigToml.Parse("schema_version = 2\n[states.running]\neffect = \"flowing_water\"\n");
Require(legacyV2.SchemaVersion == 5, "Legacy schema v2 must migrate in memory without rewriting the file.");

var unsafeRejected = false;
try
{
    var unsafeConfig = StatusLabConfig.CreateDefault();
    unsafeConfig.ProfileSwitch.Mode = K15LightingMode.MonoWater;
    unsafeConfig.Validate();
}
catch (InvalidDataException ex)
{
    unsafeRejected = ex.Message.Contains("Lighting Lab", StringComparison.OrdinalIgnoreCase);
}
Require(unsafeRejected, "Research-only mode must fail notifier validation with a Lighting Lab hint.");

Require(K15HidProtocol.HorseRaceMode == 0x83 && K15HidProtocol.MonoWaterMode == 0x83,
    "Native 0x83 must preserve historical alias while using OEM Horse race naming.");
Require(K15HidProtocol.ModeCode(K15LightingMode.FlowingWater) == 0x82, "Flowing Water mode code changed.");
Require(K15HidProtocol.ModeCode(K15LightingMode.CycleBreathing) == 0x85, "Cycle breathing mode code changed.");

var palette = new LightingEffectConfig
{
    Mode = K15LightingMode.FlowingWater,
    Brightness = 6,
    Speed = 7,
    Direction = 0,
    Colors = ["#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#800080", "#00FFFF", "#FFFFFF"],
    PaletteMask = 0b00000101
};
var paletteRecord = K15HidProtocol.CreateEffectRecord(palette, WireColorOrder.RGB);
Require(paletteRecord[3] == 0b00000101, "Lighting Lab explicit palette mask must be written verbatim.");
Require(paletteRecord[4] == 0xFF && paletteRecord[5] == 0 && paletteRecord[6] == 0, "Palette slot 1 RGB encoding changed.");
Require(paletteRecord[10] == 0 && paletteRecord[11] == 0 && paletteRecord[12] == 0xFF, "Palette slot 3 RGB encoding changed.");

var framed = K15HidProtocol.FrameReport(0x09, 0x12, 0, 0x0064, new byte[] { 1, 2, 3 });
Require(framed.Length == 41 && framed[0] == 0x06, "HID report framing changed.");
Require(K15HidProtocol.IsSupportedDevice(0xB6A4, 0x4100), "Physical K15 VID/PID must be accepted.");
Require(!K15HidProtocol.IsSupportedDevice(0x1234, 0x4100), "Unrelated VID must be rejected.");

Console.WriteLine("RC1 approval + session-aware reducer + 30s DONE + RGB policy + HID tests: PASS");
