using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var config = StatusLabConfig.CreateDefault();
config.Validate();

Require(config.Profiles.A.ManagedNormal && config.Profiles.B.ManagedNormal,
    "Managed NORMAL must default ON for both physical profiles.");
Require(config.Profiles.A.Color == "#FF0000", "Profile A canonical color must remain RED.");
Require(config.Profiles.B.Color == "#0000FF", "Profile B canonical color must remain BLUE.");
Require(config.Profiles.A.NormalBrightness == 6 && config.Profiles.B.NormalBrightness == 6,
    "Canonical NORMAL brightness default must be 6 for A/B.");

var normalA = config.GetCanonicalNormal(0);
var normalB = config.GetCanonicalNormal(1);
Require(normalA.Mode == K15LightingMode.Constant && normalB.Mode == K15LightingMode.Constant,
    "Managed NORMAL must always render Constant mode.");
Require(normalA.Colors.SequenceEqual(new[] { "#FF0000" }), "Profile A NORMAL must render RED only.");
Require(normalB.Colors.SequenceEqual(new[] { "#0000FF" }), "Profile B NORMAL must render BLUE only.");
Require(normalA.PaletteMask == 0x01 && normalB.PaletteMask == 0x01,
    "Managed NORMAL must use exactly one color slot.");

var recordA = K15HidProtocol.CreateEffectRecord(normalA, WireColorOrder.RGB);
var recordB = K15HidProtocol.CreateEffectRecord(normalB, WireColorOrder.RGB);
Require(recordA[2] == 0 && recordB[2] == 0,
    "Brightness 6 must encode as hardware brightness byte 0.");
Require(recordA[3] == 0x01 && recordA[4] == 0xFF && recordA[5] == 0 && recordA[6] == 0,
    "Profile A canonical Constant record must encode RED.");
Require(recordB[3] == 0x01 && recordB[4] == 0 && recordB[5] == 0 && recordB[6] == 0xFF,
    "Profile B canonical Constant record must encode BLUE.");

var poisonedSnapshotHeader = new byte[K15HidProtocol.LightingRecordSize];
poisonedSnapshotHeader[0] = K15HidProtocol.FlowingWaterMode;
var repairedAHeader = K15HidProtocol.CreateEffectHeader(poisonedSnapshotHeader, normalA);
var repairedBHeader = K15HidProtocol.CreateEffectHeader(poisonedSnapshotHeader, normalB);
Require(repairedAHeader[0] == K15HidProtocol.ConstantMode && repairedBHeader[0] == K15HidProtocol.ConstantMode,
    "Canonical NORMAL must override a poisoned Flowing Water snapshot header with Constant mode.");

var toml = ConfigToml.Serialize(config);
Require(toml.Contains("managed_normal = true", StringComparison.Ordinal),
    "Canonical TOML must persist managed_normal policy.");
Require(toml.Contains("normal_brightness = 6", StringComparison.Ordinal),
    "Canonical TOML must persist NORMAL brightness.");
var roundTrip = ConfigToml.Parse(toml);
Require(roundTrip.Profiles.A.ManagedNormal && roundTrip.Profiles.B.ManagedNormal,
    "TOML round-trip must preserve managed NORMAL.");

var legacyV4 = ConfigToml.Parse("""
schema_version = 4
[profiles.A]
color = "#FF0000"
[profiles.B]
color = "#0000FF"
""");
Require(legacyV4.Profiles.A.ManagedNormal && legacyV4.Profiles.B.ManagedNormal,
    "Existing schema-v4 config without new keys must inherit managed NORMAL defaults in memory.");

var optOut = ConfigToml.Parse("""
schema_version = 4
[profiles.B]
color = "#0000FF"
managed_normal = false
normal_brightness = 3
""");
Require(!optOut.Profiles.B.ManagedNormal && optOut.Profiles.B.NormalBrightness == 3,
    "Owner must be able to opt a profile out of managed NORMAL and preserve custom brightness.");

var invalidBrightnessRejected = false;
try
{
    var invalid = StatusLabConfig.CreateDefault();
    invalid.Profiles.B.NormalBrightness = 7;
    invalid.Validate();
}
catch (InvalidDataException)
{
    invalidBrightnessRejected = true;
}
Require(invalidBrightnessRejected, "Canonical NORMAL brightness must fail closed outside 1..6.");

Console.WriteLine("Canonical NORMAL A/B config + poisoned snapshot override tests: PASS");
