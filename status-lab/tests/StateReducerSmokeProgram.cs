using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static StatusInputEvent Hook(DateTimeOffset t, string name) => new(t, "codex_hook", name);
static StatusInputEvent Notification(DateTimeOffset t, string name, uint id, bool error = false) =>
    new(t, "windows_notification", name, id, "OpenAI.Codex_test", error);

var t = DateTimeOffset.Parse("2026-08-24T17:30:00Z");
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
Require(reducer.State == K15NormalizedState.DonePendingAttention,
    "Toast keywords must not create semantic ERROR.");
reducer.Apply(Notification(t.AddSeconds(5), "windows_notification_removed", 101));
Require(reducer.State == K15NormalizedState.Normal, "Removing tracked completion notification must restore NORMAL.");

var timeoutReducer = new StateReducer(10);
timeoutReducer.Apply(Hook(t.AddSeconds(20), "UserPromptSubmit"));
timeoutReducer.Apply(Hook(t.AddSeconds(21), "Stop"));
Require(timeoutReducer.Tick(t.AddSeconds(30)) is null, "DONE must remain before timeout.");
Require(timeoutReducer.Tick(t.AddSeconds(31.1)) is not null && timeoutReducer.State == K15NormalizedState.Normal,
    "DONE timeout must restore NORMAL.");

var report = K15HidProtocol.FrameReport(0x09, 0x12, 0, 0x0064, new byte[] { 1, 2, 3 });
Require(report.Length == 41, "HID report must be 41 bytes.");
Require(report[0] == 0x06 && report[3] == 0x09 && report[4] == 0x12, "HID report header mismatch.");
Require(report[6] == 0x64 && report[7] == 0x00 && report[8] == 3, "HID report address mismatch.");

var config = StatusLabConfig.CreateDefault();
config.Validate();
Require(config.SchemaVersion == 2, "TOML config schema must be v2.");
Require(config.WireColorOrder == WireColorOrder.RGB, "Physical K15 default must use RGB order.");
Require(config.Profiles.A.Color == "#FF0000", "Profile A identity must be RED.");
Require(config.Profiles.B.Color == "#0000FF", "Profile B identity must be BLUE.");
Require(config.States.Running.Mode == K15LightingMode.MonoWater,
    "RUNNING default must be Mono Water candidate, not Tetris.");
Require(config.ProfileSwitch.Mode == K15LightingMode.FlowingWater,
    "Profile switch default must be controlled single-color Flowing Water.");
Require(!config.ActivationSignal.Enabled, "Multicolor activation handshake must be off by default.");
Require(config.States.Running.Colors.Length == 0 && config.States.Waiting.Colors.Length == 0,
    "State config must not own semantic colors.");
Require(StatusLabConfig.MaxNotifierColors == 2, "Notifier architecture must cap explicit palettes at two colors.");

foreach (var safe in new[]
         {
             K15LightingMode.Constant,
             K15LightingMode.FlowingWater,
             K15LightingMode.MonoWater,
             K15LightingMode.SingleColorBreathing,
             K15LightingMode.Off
         })
{
    Require(StatusLabConfig.IsControlledPaletteMode(safe), $"{safe} must remain notifier-safe.");
}

foreach (var rejected in new[]
         {
             K15LightingMode.CycleBreathing,
             K15LightingMode.TetrisBlocks,
             K15LightingMode.Neon,
             K15LightingMode.Ambilight
         })
{
    Require(!StatusLabConfig.IsControlledPaletteMode(rejected), $"{rejected} must be rejected for notifier use.");
}

var rainbowRejected = false;
try
{
    var unsafeConfig = StatusLabConfig.CreateDefault();
    unsafeConfig.ProfileSwitch.Mode = K15LightingMode.TetrisBlocks;
    unsafeConfig.Validate();
}
catch (InvalidDataException ex)
{
    rainbowRejected = ex.Message.Contains("controlled 1-2 color", StringComparison.OrdinalIgnoreCase);
}
Require(rainbowRejected, "Uncontrolled multicolor notifier modes must fail config validation.");

var runningA = config.RenderForProfile(0, config.States.Running);
var runningB = config.RenderForProfile(1, config.States.Running);
Require(runningA.Colors.SequenceEqual(new[] { "#FF0000" }), "Profile A RUNNING must render RED only.");
Require(runningB.Colors.SequenceEqual(new[] { "#0000FF" }), "Profile B RUNNING must render BLUE only.");
var waitingA = config.RenderForProfile(0, config.States.Waiting);
var doneB = config.RenderForProfile(1, config.States.Done);
Require(waitingA.Colors.Single() == "#FF0000", "Profile A WAITING must stay RED.");
Require(doneB.Colors.Single() == "#0000FF", "Profile B DONE must stay BLUE.");
Require(config.RenderForProfile(0, config.ProfileSwitch).Colors.Length == 1,
    "Profile-switch default must render exactly one profile color.");

var toml = ConfigToml.Serialize(config);
Require(toml.Contains("[profiles.A]") && toml.Contains("color = \"#FF0000\""),
    "Annotated TOML must expose profile colors.");
Require(toml.Contains("[states.running]") && toml.Contains("effect = \"mono_water\""),
    "Annotated TOML must expose state effects.");
Require(toml.Contains("[profile_switch]") && toml.Contains("effect = \"flowing_water\""),
    "Annotated TOML must use controlled Flowing Water for profile switching.");
Require(!toml.Contains("[states.running]\ncolor", StringComparison.Ordinal),
    "State TOML must not contain color keys.");
Require(toml.Contains("1-2 цвета", StringComparison.OrdinalIgnoreCase),
    "Canonical TOML must explain the controlled-color policy.");
Require(toml.Contains("#"), "Canonical TOML must contain human comments.");

var parsed = ConfigToml.Parse(toml);
Require(parsed.Profiles.A.Color == "#FF0000" && parsed.Profiles.B.Color == "#0000FF",
    "TOML round-trip must preserve profile colors.");
Require(parsed.States.Waiting.Mode == K15LightingMode.SingleColorBreathing,
    "TOML round-trip must preserve WAITING effect.");
Require(parsed.ProfileSwitch.Mode == K15LightingMode.FlowingWater && parsed.ProfileSwitch.DurationSeconds == 2,
    "TOML round-trip must preserve controlled profile-switch policy.");

var invalidRejected = false;
try
{
    ConfigToml.Parse("schema_version = 2\n[states.running]\nbrightness = 99\n");
}
catch (InvalidDataException ex)
{
    invalidRejected = ex.Message.Contains("brightness", StringComparison.OrdinalIgnoreCase);
}
Require(invalidRejected, "Invalid TOML must fail with a useful path-specific error.");

var unsafeTomlRejected = false;
try
{
    ConfigToml.Parse("schema_version = 2\n[profile_switch]\neffect = \"neon\"\n");
}
catch (InvalidDataException ex)
{
    unsafeTomlRejected = ex.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase);
}
Require(unsafeTomlRejected, "Unsafe multicolor TOML modes must be rejected instead of merely warned.");

Require(K15HidProtocol.ModeCode(K15LightingMode.MonoWater) == 0x83,
    "Mono Water must map to native mode 0x83.");
Require(K15HidProtocol.ModeCode(K15LightingMode.TetrisBlocks) == 0x86,
    "Tetris mapping remains available only for low-level research/forensics.");
Require(K15HidProtocol.ModeRecordAddress(K15LightingMode.FlowingWater) == 50,
    "Flowing Water detail record must use address 2*25.");

var runningRecord = K15HidProtocol.CreateEffectRecord(runningA, WireColorOrder.RGB);
Require(runningRecord[4] == 0xFF && runningRecord[5] == 0x00 && runningRecord[6] == 0x00,
    "Rendered Profile A color must encode physical red as RGB.");
var runningBRecord = K15HidProtocol.CreateEffectRecord(runningB, WireColorOrder.RGB);
Require(runningBRecord[4] == 0x00 && runningBRecord[5] == 0x00 && runningBRecord[6] == 0xFF,
    "Rendered Profile B color must encode physical blue as RGB.");
var legacyRecord = K15HidProtocol.CreateEffectRecord(runningA, WireColorOrder.GRB);
Require(legacyRecord[4] == 0x00 && legacyRecord[5] == 0xFF,
    "GRB compatibility option must remain available.");

var originalHeader = Enumerable.Range(0, 25).Select(value => (byte)value).ToArray();
var runningHeader = K15HidProtocol.CreateEffectHeader(originalHeader, runningA);
Require(runningHeader[0] == K15HidProtocol.MonoWaterMode,
    "RUNNING renderer must select Mono Water candidate mode.");
Require(runningHeader.Skip(1).SequenceEqual(originalHeader.Skip(1)),
    "Effect header must preserve non-mode bytes.");
Require(K15HidProtocol.IsSupportedDevice(0xB6A4, 0x4100), "Physical K15 VID/PID must be accepted.");
Require(!K15HidProtocol.IsSupportedDevice(0x1234, 0x4100), "Unrelated VID must be rejected.");

Console.WriteLine("State reducer + controlled-color TOML/profile renderer + HID protocol smoke tests: PASS");
