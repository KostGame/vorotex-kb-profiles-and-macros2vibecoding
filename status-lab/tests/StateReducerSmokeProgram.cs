using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static StatusInputEvent Hook(DateTimeOffset t, string name) => new(t, "codex_hook", name);
static StatusInputEvent Notification(DateTimeOffset t, string name, uint id, bool error = false) =>
    new(t, "windows_notification", name, id, "OpenAI.Codex_test", error);

var t = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
var reducer = new StateReducer();
Require(reducer.State == K15NormalizedState.Normal, "Initial state must be NORMAL.");
reducer.Apply(Hook(t, "UserPromptSubmit"));
Require(reducer.State == K15NormalizedState.Running, "UserPromptSubmit must enter RUNNING.");
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

var config = StatusLabConfig.CreateDefault();
config.Validate();
Require(config.SchemaVersion == 2, "TOML schema must be v2.");
Require(config.WireColorOrder == WireColorOrder.RGB, "Physical K15 default must use RGB.");
Require(config.Profiles.A.Color == "#FF0000" && config.Profiles.B.Color == "#0000FF", "Profile identity colors changed.");
Require(config.States.Running.Mode == K15LightingMode.SingleColorBreathing, "RUNNING safe default must use explicit single-color mode.");
Require(config.ProfileSwitch.Mode == K15LightingMode.SingleColorBreathing, "Profile switch safe default must use explicit single-color mode.");
Require(!config.ActivationSignal.Enabled, "Activation must remain off by default.");
Require(StatusLabConfig.IsControlledPaletteMode(K15LightingMode.Constant), "Constant must remain notifier-safe.");
Require(StatusLabConfig.IsControlledPaletteMode(K15LightingMode.FlowingWater), "Flowing Water must remain notifier-safe when an explicit palette is supplied.");
Require(StatusLabConfig.IsControlledPaletteMode(K15LightingMode.SingleColorBreathing), "Single-color breathing must remain notifier-safe.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.MonoWater), "Native 0x83/Horse race must be research-only.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.CycleBreathing), "Cycle breathing must be research-only.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.TetrisBlocks), "Tetris must be research-only.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.Neon), "Neon must be research-only.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.Ambilight), "Ambilight must be research-only.");

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

var legacyUnsafeRejected = false;
try
{
    ConfigToml.Parse("schema_version = 2\n[states.running]\neffect = \"mono_water\"\n");
}
catch (InvalidDataException ex)
{
    legacyUnsafeRejected = ex.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase);
}
Require(legacyUnsafeRejected, "Legacy mono_water config must be preserved/rejected, not silently treated as safe.");

var runningA = config.RenderForProfile(0, config.States.Running);
var runningB = config.RenderForProfile(1, config.States.Running);
Require(runningA.Colors.SequenceEqual(new[] { "#FF0000" }), "Profile A renderer must use red only.");
Require(runningB.Colors.SequenceEqual(new[] { "#0000FF" }), "Profile B renderer must use blue only.");
Require(runningA.PaletteMask is null, "Notifier renderer must not inherit research palette masks.");

var toml = ConfigToml.Serialize(config);
Require(toml.Contains("effect = \"single_color_breathing\""), "Canonical TOML must use safe single-color defaults.");
Require(toml.Contains("НИКОГДА программно не переключает", StringComparison.Ordinal), "Canonical TOML must document observe-only profile policy.");
Require(toml.Contains("Lighting Lab", StringComparison.OrdinalIgnoreCase), "Canonical TOML must route research modes to Lighting Lab.");
Require(!toml.Contains("[states.running]\ncolor", StringComparison.Ordinal), "State TOML must not own colors.");
var roundTrip = ConfigToml.Parse(toml);
Require(roundTrip.ProfileSwitch.Mode == K15LightingMode.SingleColorBreathing, "TOML round-trip lost profile-switch mode.");

Require(K15HidProtocol.HorseRaceMode == 0x83 && K15HidProtocol.MonoWaterMode == 0x83,
    "Native 0x83 must preserve historical alias while using OEM Horse race naming.");
Require(K15HidProtocol.ModeCode(K15LightingMode.FlowingWater) == 0x82, "Flowing Water mode code changed.");
Require(K15HidProtocol.ModeRecordAddress(K15LightingMode.FlowingWater) == 50, "Flowing Water record address changed.");

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

Console.WriteLine("State reducer + safe notifier config + Lighting Lab palette-mask protocol tests: PASS");
