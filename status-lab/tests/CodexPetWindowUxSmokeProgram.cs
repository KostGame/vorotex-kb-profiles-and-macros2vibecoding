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

Require(CodexPetTaskSurfacePolicy.Cycle(PetTaskSurfaceState.Collapsed) == PetTaskSurfaceState.Stacked &&
        CodexPetTaskSurfacePolicy.Cycle(PetTaskSurfaceState.Stacked) == PetTaskSurfaceState.Expanded &&
        CodexPetTaskSurfacePolicy.Cycle(PetTaskSurfaceState.Expanded) == PetTaskSurfaceState.Collapsed,
    "TASK_SURFACE_STATE_CYCLE");
Require(CodexPetTaskSurfacePolicy.Select(PetTaskSurfaceState.Collapsed) == PetTaskSurfaceState.Collapsed &&
        CodexPetTaskSurfacePolicy.Select(PetTaskSurfaceState.Expanded) == PetTaskSurfaceState.Expanded,
    "TASK_SURFACE_DIRECT_SELECTION");
Require(CodexPetTaskSurfacePolicy.DefaultState == PetTaskSurfaceState.Stacked &&
        !CodexPetTaskSurfacePolicy.IsVisible(PetTaskSurfaceState.Collapsed, 5) &&
        !CodexPetTaskSurfacePolicy.IsVisible(PetTaskSurfaceState.Expanded, 0) &&
        CodexPetTaskSurfacePolicy.IsVisible(PetTaskSurfaceState.Stacked, 1),
    "TASK_SURFACE_ZERO_COUNT_HIDES");

foreach (var count in new[] { 1, 2, 5, 6 })
{
    var expanded = CodexPetTaskSurfacePolicy.Layout(PetTaskSurfaceState.Expanded, count);
    var expectedRows = Math.Min(5, count);
    Require(expanded.Cards.Count == expectedRows &&
            expanded.Cards.All(card => card.ShowsText) &&
            expanded.OverflowCount == Math.Max(0, count - 5),
        $"EXPANDED_GEOMETRY_{count}");
}

var stackedOne = CodexPetTaskSurfacePolicy.Layout(PetTaskSurfaceState.Stacked, 1);
var stackedTwo = CodexPetTaskSurfacePolicy.Layout(PetTaskSurfaceState.Stacked, 2);
var stackedMany = CodexPetTaskSurfacePolicy.Layout(PetTaskSurfaceState.Stacked, 3);
Require(stackedOne.Cards.Count == 1 && stackedOne.Cards[0].IsTopCard && stackedOne.Cards[0].ShowsText,
    "STACKED_ONE_TOP_CARD_READABLE");
Require(stackedTwo.Cards.Count == 2 && CodexPetTaskSurfacePolicy.StackedHiddenLayerCount(2) == 1 &&
        stackedTwo.Cards[^1].IsTopCard && stackedTwo.Cards[^1].ShowsText,
    "STACKED_TWO_ONE_LAYER");
Require(stackedMany.Cards.Count == 3 && CodexPetTaskSurfacePolicy.StackedHiddenLayerCount(99) == 2 &&
        stackedMany.Cards.Count <= 3 && stackedMany.Cards[^1].IsTopCard &&
        stackedMany.Cards.Take(2).All(card => !card.ShowsText),
    "STACKED_THREE_BOUNDED_LAYERS");

var workingArea = new Rectangle(0, 0, 1920, 1080);
var below = TaskPanelPlacementPolicy.Place(new Rectangle(800, 300, 192, 192), new Size(320, 220), workingArea);
Require(below.Side == TaskPanelPlacementSide.Below && below.Bounds.Bottom <= workingArea.Bottom,
    "PLACEMENT_BELOW_WHEN_SPACE_AVAILABLE");
var above = TaskPanelPlacementPolicy.Place(new Rectangle(800, 900, 192, 192), new Size(320, 220), workingArea);
Require(above.Side == TaskPanelPlacementSide.Above && above.Bounds.Top >= workingArea.Top,
    "PLACEMENT_ABOVE_AT_BOTTOM_EDGE");
var neither = TaskPanelPlacementPolicy.Place(new Rectangle(100, 130, 100, 150), new Size(250, 220),
    new Rectangle(0, 0, 400, 300));
Require(neither.Side == TaskPanelPlacementSide.Above && neither.Bounds.Top == 0 &&
        neither.Bounds.Bottom <= 300,
    "PLACEMENT_LARGER_SIDE_AND_CLAMP");
var negative = TaskPanelPlacementPolicy.Place(new Rectangle(-1600, 200, 192, 192), new Size(320, 220),
    new Rectangle(-1920, 0, 1920, 1080));
Require(negative.Bounds.Left >= -1920 && negative.Bounds.Right <= 0,
    "PLACEMENT_NEGATIVE_MONITOR");
var secondary = TaskPanelPlacementPolicy.Place(new Rectangle(2400, -900, 192, 192), new Size(320, 220),
    new Rectangle(1920, -1080, 2560, 1080));
Require(secondary.Bounds.Left >= 1920 && secondary.Bounds.Top >= -1080 && secondary.Bounds.Bottom <= 0,
    "PLACEMENT_SECONDARY_MONITOR");
var movedPrimary = TaskPanelPlacementPolicy.Place(new Rectangle(100, 300, 192, 192), new Size(320, 220),
    new Rectangle(0, 0, 1920, 1080));
var movedSecondary = TaskPanelPlacementPolicy.Place(new Rectangle(2000, 300, 192, 192), new Size(320, 220),
    new Rectangle(1920, 0, 2560, 1440));
Require(movedPrimary.Bounds.Location != movedSecondary.Bounds.Location &&
        movedSecondary.Bounds.Left >= 1920,
    "PLACEMENT_RECOMPUTES_AFTER_MONITOR_MOVE");
var oversized = TaskPanelPlacementPolicy.Place(new Rectangle(100, 100, 100, 100), new Size(1000, 1000),
    new Rectangle(-50, -40, 400, 300));
Require(oversized.Bounds == new Rectangle(-50, -40, 400, 300),
    "PLACEMENT_OVERSIZED_PANEL_BOUNDED");

var expectedPetSizes = new Dictionary<PetSizePreset, Size>
{
    [PetSizePreset.ExtraSmall] = new(96, 96),
    [PetSizePreset.Small] = new(128, 128),
    [PetSizePreset.Medium] = new(160, 160),
    [PetSizePreset.Large] = new(192, 192),
    [PetSizePreset.ExtraLarge] = new(256, 256),
    [PetSizePreset.Huge] = new(320, 320)
};
Require(CodexPetSizePolicy.DefaultPreset == PetSizePreset.Medium &&
        expectedPetSizes.Count == Enum.GetValues<PetSizePreset>().Length &&
        expectedPetSizes.All(pair => CodexPetSizePolicy.Geometry(pair.Key).WindowSize == pair.Value),
    "PET_SIZE_PRESETS_AND_DEFAULT");
Require(expectedPetSizes.Keys.All(preset => !string.IsNullOrWhiteSpace(CodexPetSizePolicy.Label(preset))) &&
        CodexPetSizePolicy.Label(PetSizePreset.ExtraSmall) == "Очень маленький" &&
        CodexPetSizePolicy.Label(PetSizePreset.ExtraLarge) == "Очень большой" &&
        CodexPetSizePolicy.Label(PetSizePreset.Huge) == "Огромный",
    "PET_SIZE_MENU_LABELS");
var ordinaryKeyIds = new[]
{
    "key-1", "key-2", "key-3", "key-4", "key-5", "key-6",
    "key-7", "key-8", "key-9", "key-0", "key-dot", "minus", "plus"
};
var ordinaryKeys = ordinaryKeyIds.Select(id => MiniK15ControlLayout.Controls.Single(control => control.Id == id)).ToArray();
Require(ordinaryKeys.All(control => control.Kind == MiniK15ControlKind.Key &&
        control.Width == MiniK15ControlLayout.OrdinaryKeyWidth &&
        control.Height == MiniK15ControlLayout.OrdinaryKeyHeight) &&
        ordinaryKeys.Select(control => (control.Width, control.Height)).Distinct().Count() == 1,
    "ALL_ORDINARY_KEYCAPS_SHARE_ONE_UNIT");
Require(MiniK15ControlLayout.Controls.Single(control => control.Id == "enter") is
            { Kind: MiniK15ControlKind.WideEnter, Width: MiniK15ControlLayout.EnterWidth } &&
        MiniK15ControlLayout.Controls.Single(control => control.Id == "long-bottom") is
            { Kind: MiniK15ControlKind.LongBottomKey, Width: MiniK15ControlLayout.SpaceWidth } &&
        MiniK15ControlLayout.Controls.Single(control => control.Id == "rotary") is
            { Kind: MiniK15ControlKind.Rotary, Width: MiniK15ControlLayout.RotaryWidth } &&
        MiniK15ControlLayout.Controls.Single(control => control.Id == "joystick") is
            { Kind: MiniK15ControlKind.Joystick, Width: MiniK15ControlLayout.JoystickWidth } &&
        MiniK15ControlLayout.Controls.Count(control => control.Kind != MiniK15ControlKind.Key) == 4,
    "ONLY_FOUR_INTENTIONAL_SPECIAL_CONTROLS");

var topRow = MiniK15ControlLayout.Controls.Where(control => control.Band == MiniK15ControlBand.Top)
    .OrderBy(control => control.X).ToArray();
var middleRow = MiniK15ControlLayout.Controls.Where(control => control.Band == MiniK15ControlBand.Middle)
    .OrderBy(control => control.X).ToArray();
var bottomRow = MiniK15ControlLayout.Controls.Where(control => control.Band == MiniK15ControlBand.Bottom)
    .OrderBy(control => control.X).ToArray();
static int Gap(MiniK15Control left, MiniK15Control right) => right.X - (left.X + left.Width);
static bool UniformGutters(IReadOnlyList<MiniK15Control> row, int expectedGap) =>
    row.Zip(row.Skip(1), Gap).All(gap => gap == expectedGap);
static int RightEdge(IReadOnlyList<MiniK15Control> row) => row.Max(control => control.X + control.Width);
Require(topRow.Length == 7 && middleRow.Length == 6 &&
        UniformGutters(topRow, MiniK15ControlLayout.RowGutter) &&
        UniformGutters(middleRow, MiniK15ControlLayout.RowGutter) &&
        Math.Abs(topRow[0].X - middleRow[0].X) <= 1 &&
        Math.Abs(RightEdge(topRow) - RightEdge(middleRow)) <= 1,
    "TOP_MIDDLE_COMPOSED_ROWS_FILL_WITH_MATCHED_GUTTERS_AND_EDGES");
Require(bottomRow.Length == 4 && Gap(bottomRow[0], bottomRow[1]) == MiniK15ControlLayout.RowGutter &&
        Gap(bottomRow[1], bottomRow[2]) == MiniK15ControlLayout.RowGutter &&
        bottomRow[0].X == MiniK15ControlLayout.BottomOuterMargin &&
        bottomRow[2].Width == MiniK15ControlLayout.ChassisWidth - 2 * MiniK15ControlLayout.BottomOuterMargin -
            MiniK15ControlLayout.JoystickWidth - MiniK15ControlLayout.BottomJoystickGap -
            2 * MiniK15ControlLayout.OrdinaryKeyWidth - 2 * MiniK15ControlLayout.RowGutter &&
        bottomRow[2].Width > 4 * MiniK15ControlLayout.OrdinaryKeyWidth &&
        Gap(bottomRow[2], bottomRow[3]) == MiniK15ControlLayout.BottomJoystickGap &&
        bottomRow[3].X + bottomRow[3].Width ==
            MiniK15ControlLayout.ChassisWidth - MiniK15ControlLayout.BottomOuterMargin,
    "BOTTOM_LEFT_CLUSTER_AND_DEDICATED_RIGHT_CELL");
Require(Math.Abs(MiniK15ControlLayout.RotaryWidth / (double)MiniK15ControlLayout.OrdinaryKeyWidth - 1.4) < 0.01 &&
        Math.Abs(MiniK15ControlLayout.JoystickWidth / (double)MiniK15ControlLayout.OrdinaryKeyWidth - 1.4) < 0.01 &&
        Math.Abs(MiniK15ControlLayout.EnterWidth / (double)MiniK15ControlLayout.OrdinaryKeyWidth - 2.4) < 0.01 &&
        MiniK15ControlLayout.SpaceWidth / (double)MiniK15ControlLayout.OrdinaryKeyWidth > 4.0,
    "SPECIAL_CONTROL_HARDWARE_RATIOS");
foreach (var preset in Enum.GetValues<PetSizePreset>())
{
    var geometry = CodexPetSizePolicy.Geometry(preset);
    var controlBounds = MiniK15ControlLayout.Controls.Select(control =>
        (Control: control, Bounds: CodexPetSizePolicy.ControlBounds(geometry.KeyboardBodyBounds, control))).ToArray();
    Require(new Rectangle(Point.Empty, geometry.WindowSize).Contains(geometry.KeyboardBodyBounds) &&
            new Rectangle(Point.Empty, geometry.WindowSize).Contains(geometry.BadgeBounds) &&
            geometry.BadgeBounds.Width >= 18 && geometry.BadgeBounds.Height >= 10 &&
            controlBounds.All(item => geometry.KeyboardBodyBounds.Contains(item.Bounds)) &&
            controlBounds.All(item => new Rectangle(Point.Empty, geometry.WindowSize).Contains(item.Bounds)),
        $"PET_SIZE_GEOMETRY_{preset}");
    Require(controlBounds.SelectMany((left, index) => controlBounds.Skip(index + 1)
                .Select(right => (left, right)))
            .All(pair => !pair.left.Bounds.IntersectsWith(pair.right.Bounds)),
        $"PET_CONTROL_BOUNDS_DO_NOT_OVERLAP_{preset}");
    Require(controlBounds.Where(item => !string.IsNullOrWhiteSpace(item.Control.Label)).All(item =>
            item.Bounds.Width >= item.Control.Label!.Length * (item.Control.Kind == MiniK15ControlKind.LongBottomKey ? 4 : 5) + 2 &&
            item.Bounds.Height >= 10),
        $"PET_CONTROL_LABELS_FIT_{preset}");
}
Require(CodexPetSizePolicy.Select((PetSizePreset)99) == PetSizePreset.Medium, "PET_SIZE_INVALID_DEFAULTS");

var legacy = CodexPetUiPreferenceStore.Deserialize("{\"presentationState\":\"Collapsed\"}");
Require(legacy.PresentationState == PetTaskSurfaceState.Collapsed && legacy.PetSize == PetSizePreset.Medium,
    "LEGACY_STATE_ONLY_DEFAULTS_SIZE");
foreach (var preset in Enum.GetValues<PetSizePreset>())
{
    var roundtrip = CodexPetUiPreferenceStore.Deserialize(
        CodexPetUiPreferenceStore.Serialize(new CodexPetUiPreferences(PetTaskSurfaceState.Expanded, preset)));
    Require(roundtrip == new CodexPetUiPreferences(PetTaskSurfaceState.Expanded, preset),
        $"UI_PREFERENCE_SIZE_ROUNDTRIP_{preset}");
}
var invalidSize = CodexPetUiPreferenceStore.Deserialize("{\"presentationState\":\"Expanded\",\"petSize\":\"Unknown\"}");
Require(invalidSize == new CodexPetUiPreferences(PetTaskSurfaceState.Expanded, PetSizePreset.Medium),
    "INVALID_SIZE_ONLY_DEFAULTS_SIZE");
Require(CodexPetUiPreferenceStore.Deserialize("{\"presentationState\":\"Expanded\",\"petSize\":\"Small\"}").PetSize == PetSizePreset.Small &&
        CodexPetUiPreferenceStore.Deserialize("{\"presentationState\":\"Expanded\",\"petSize\":\"Medium\"}").PetSize == PetSizePreset.Medium &&
        CodexPetUiPreferenceStore.Deserialize("{\"presentationState\":\"Expanded\",\"petSize\":\"Large\"}").PetSize == PetSizePreset.Large,
    "LEGACY_PET_SIZE_STRINGS_RETAIN_MEANING");
var invalidState = CodexPetUiPreferenceStore.Deserialize("{\"presentationState\":\"Unknown\",\"petSize\":\"Small\"}");
Require(invalidState == new CodexPetUiPreferences(PetTaskSurfaceState.Stacked, PetSizePreset.Small),
    "INVALID_STATE_ONLY_DEFAULTS_STATE");
var preferenceJson = CodexPetUiPreferenceStore.Serialize(new CodexPetUiPreferences(PetTaskSurfaceState.Stacked, PetSizePreset.Medium));
Require(preferenceJson.Contains("presentationState", StringComparison.Ordinal) &&
        preferenceJson.Contains("petSize", StringComparison.Ordinal) &&
        !preferenceJson.Contains("session", StringComparison.OrdinalIgnoreCase) &&
        !preferenceJson.Contains("task", StringComparison.OrdinalIgnoreCase),
    "UI_PREFERENCE_PRESENTATION_FIELDS_ONLY");
var preferencePath = Path.Combine(Path.GetTempPath(), "vorotex-k15-pet-ui-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var preferenceStore = new CodexPetUiPreferenceStore(preferencePath);
    preferenceStore.Save(new CodexPetUiPreferences(PetTaskSurfaceState.Expanded, PetSizePreset.Large));
    Require(preferenceStore.Load() == new CodexPetUiPreferences(PetTaskSurfaceState.Expanded, PetSizePreset.Large),
        "UI_PREFERENCE_FILE_ROUNDTRIP");
}
finally
{
    if (File.Exists(preferencePath)) File.Delete(preferencePath);
}

var resolverRoot = Path.Combine(Path.GetTempPath(), "vorotex-k15-live-dashboard-" + Guid.NewGuid().ToString("N"));
var trayDirectory = Path.Combine(resolverRoot, "status-tray");
var dashboardDirectory = Path.Combine(resolverRoot, "live-dashboard");
Directory.CreateDirectory(trayDirectory);
Directory.CreateDirectory(dashboardDirectory);
var dashboardName = LiveDashboardPathPolicy.DefaultExecutableName;
var colocatedDashboard = Path.Combine(trayDirectory, dashboardName);
var siblingDashboard = Path.Combine(dashboardDirectory, dashboardName);
try
{
    File.WriteAllText(colocatedDashboard, "fake");
    File.WriteAllText(siblingDashboard, "fake");
    Require(LiveDashboardPathPolicy.Resolve(trayDirectory) == colocatedDashboard, "LIVE_DASHBOARD_COLOCATED_RESOLUTION");
    File.Delete(colocatedDashboard);
    Require(LiveDashboardPathPolicy.Resolve(trayDirectory) == siblingDashboard, "LIVE_DASHBOARD_SIBLING_RESOLUTION");
    File.Delete(siblingDashboard);
    Directory.CreateDirectory(Path.Combine(resolverRoot, "unrelated", "nested"));
    File.WriteAllText(Path.Combine(resolverRoot, "unrelated", "nested", dashboardName), "fake");
    Require(LiveDashboardPathPolicy.Resolve(trayDirectory) is null, "LIVE_DASHBOARD_MISSING_FAILS_CLOSED");
}
finally
{
    if (Directory.Exists(resolverRoot)) Directory.Delete(resolverRoot, true);
}

Console.WriteLine("CODEX_PET_WINDOW_UX_SMOKE_PASSED");
