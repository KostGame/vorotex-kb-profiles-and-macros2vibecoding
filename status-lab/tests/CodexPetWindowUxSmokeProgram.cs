using System.Drawing;
using Vorotex.K15.StatusLab;

static void Require(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS={name}");
}

var areas = new[]
{
    new Rectangle(-1920, 0, 1920, 1080),
    new Rectangle(0, 0, 1920, 1080)
};
var size = new Size(128, 128);

Require(CodexPetPositionPolicy.Resolve(new CodexPetPosition(100, 200), size, areas) == new Point(100, 200), "VALID_POSITION_RETAINED");
Require(CodexPetPositionPolicy.Resolve(new CodexPetPosition(-1800, 100), size, areas) == new Point(-1800, 100), "NEGATIVE_COORDINATE_RETAINED");
Require(CodexPetPositionPolicy.Resolve(new CodexPetPosition(4000, 4000), size, areas) == new Point(1768, 928), "OFFSCREEN_POSITION_FALLS_BACK");
Require(CodexPetPositionPolicy.IsRecoverable(new Point(1880, 100), size, areas), "PARTIALLY_RECOVERABLE_POSITION_RETAINED");
Require(!CodexPetPositionPolicy.IsRecoverable(new Point(1910, 100), size, areas), "INSUFFICIENT_VISIBLE_POSITION_REJECTED");

var serialized = CodexPetPositionStore.Serialize(new CodexPetPosition(-42, 77));
Require(CodexPetPositionStore.Deserialize(serialized) == new CodexPetPosition(-42, 77), "POSITION_SERIALIZATION_ROUNDTRIP");
Require(CodexPetPositionStore.Deserialize("not-json") is null, "INVALID_POSITION_SERIALIZATION_IGNORED");

Console.WriteLine("CODEX_PET_WINDOW_UX_SMOKE_PASSED");
