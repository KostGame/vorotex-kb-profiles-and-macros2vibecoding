using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static StatusInputEvent Hook(DateTimeOffset t, string name, string session = "session-main", string cwd = @"C:\work\main") =>
    new(t, "codex_hook", name, SessionId: session, Cwd: cwd);
static StatusInputEvent Notification(DateTimeOffset t, string name, uint id, bool error = false) =>
    new(t, "windows_notification", name, id, "OpenAI.Codex_test", error);

var t = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
var reducer = new StateReducer();
Require(reducer.State == K15NormalizedState.Normal, "Initial state must be NORMAL.");
reducer.Apply(Hook(t, "UserPromptSubmit"));
Require(reducer.State == K15NormalizedState.Running, "UserPromptSubmit must enter RUNNING.");
Require(reducer.FocusedSessionId == "session-main", "Main task session must become focused.");
reducer.Apply(Hook(t.AddSeconds(1), "PermissionRequest"));
Require(reducer.State == K15NormalizedState.Waiting, "PermissionRequest must enter WAITING.");
reducer.Apply(Hook(t.AddSeconds(2), "PostToolUse"));
Require(reducer.State == K15NormalizedState.Running, "PostToolUse must resume RUNNING after approval.");
reducer.Apply(Hook(t.AddSeconds(3), "Stop"));
Require(reducer.State == K15NormalizedState.DonePendingAttention, "Stop must enter DONE.");
reducer.Apply(Notification(t.AddSeconds(4), "windows_notification_added", 101, error: true));
Require(reducer.State == K15NormalizedState.DonePendingAttention, "Toast keywords must not create semantic ERROR.");
reducer.Apply(Notification(t.AddSeconds(5), "windows_notification_removed", 101));
Require(reducer.State == K15NormalizedState.Normal, "Removing tracked completion notification must restore NORMAL.");

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
    Hook(t.AddSeconds(2), "PostToolUse", "rehydrate-main", @"D:\AI_AGENT_PROJECTS\rehydrate"),
    Hook(t.AddSeconds(3), "UserPromptSubmit", "rehydrate-memory", @"C:\Users\Desktop\.codex-agentloop\memories"),
    Hook(t.AddSeconds(4), "SessionEnd", "rehydrate-memory", @"C:\Users\Desktop\.codex-agentloop\memories")
});
Require(rehydrated.State == K15NormalizedState.Running,
    "Startup replay must recover a still-running main Codex session.");
Require(rehydrated.FocusedSessionId == "rehydrate-main", "Rehydrate must recover foreground main session focus.");
Require(StateReducer.IsInternalCwd(@"C:\Users\Desktop\.codex-agentloop\memories"),
    "AgentLoop memories cwd must be classified internal.");
Require(!StateReducer.IsInternalCwd(@"D:\AI_AGENT_PROJECTS\task"), "Normal project cwd must not be internal.");

var config = StatusLabConfig.CreateDefault();
config.Validate();
Require(config.SchemaVersion == 3, "Canonical TOML schema must be v3.");
Require(config.WireColorOrder == WireColorOrder.RGB, "Physical K15 default must use RGB.");
Require(config.Profiles.A.Color == "#FF0000" && config.Profiles.B.Color == "#0000FF", "Profile identity colors changed.");
Require(config.States.Running.Mode == K15LightingMode.FlowingWater, "RUNNING default must use Flowing Water.");
Require(config.States.Running.Palette == PaletteSource.Profile, "RUNNING must use active profile color.");
Require(config.States.Waiting.Mode == K15LightingMode.SingleColorBreathing && config.States.Waiting.Speed == 7,
    "WAITING must use fast single-color breathing speed 7.");
Require(config.States.Done.Mode == K15LightingMode.SingleColorBreathing && config.States.Done.Speed == 5,
    "DONE must use slower single-color breathing speed 5.");
Require(config.StopSignal.Mode == K15LightingMode.CycleBreathing &&
        config.StopSignal.Palette == PaletteSource.ProfilePair,
    "STOP signal must use two-color Cycle breathing.");
Require(config.ActivationSignal.Enabled && config.ActivationSignal.Mode == K15LightingMode.FlowingWater &&
        config.ActivationSignal.Palette == PaletteSource.ProfilePair,
    "RGB activation must use two-color Flowing Water.");
Require(config.ProfileSwitch.Palette == PaletteSource.Profile,
    "Profile switch must remain single active-profile color.");
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
Require(toml.Contains("schema_version = 3", StringComparison.Ordinal), "Canonical TOML must use schema v3.");
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

var legacyV2 = ConfigToml.Parse("schema_version = 2\n[states.running]\neffect = \"flowing_water\"\n");
Require(legacyV2.SchemaVersion == 3, "Legacy schema v2 must migrate in memory without rewriting the file.");

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

Console.WriteLine("Session-aware reducer + profile-pair RGB policy + HID protocol tests: PASS");
