using System.Drawing;
using System.Drawing.Drawing2D;
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
var blueBadgeStyle = CodexPetBadgeVisualPolicy.Resolve(Color.Blue);
var redBadgeStyle = CodexPetBadgeVisualPolicy.Resolve(Color.Red);
Require(!CodexPetBadgeRenderer.ShouldDraw(0) &&
        new[] { 1, 99, 100 }.All(CodexPetBadgeRenderer.ShouldDraw),
    "TASK_BADGE_HIDDEN_AT_ZERO_AND_SHOWN_FOR_POSITIVE_COUNTS");
Require(blueBadgeStyle.ContrastRatio >= 4.5 && redBadgeStyle.ContrastRatio >= 4.5 &&
        Math.Abs(blueBadgeStyle.ContrastRatio - CodexPetBadgeVisualPolicy.ContrastRatio(
            blueBadgeStyle.Foreground, blueBadgeStyle.Fill)) < 0.001 &&
        Math.Abs(redBadgeStyle.ContrastRatio - CodexPetBadgeVisualPolicy.ContrastRatio(
            redBadgeStyle.Foreground, redBadgeStyle.Fill)) < 0.001 &&
        blueBadgeStyle.Fill != redBadgeStyle.Fill && blueBadgeStyle.Outline != redBadgeStyle.Outline,
    $"TASK_BADGE_PROFILE_DERIVED_CONTRAST_BLUE_{blueBadgeStyle.ContrastRatio:F2}_RED_{redBadgeStyle.ContrastRatio:F2}");
var ordinaryKeyIds = new[]
{
    "key-1", "key-2", "key-3", "key-4", "key-5", "key-6",
    "key-7", "key-8", "key-9", "key-0", "key-dot", "minus", "plus"
};
var ordinaryKeys = ordinaryKeyIds.Select(id => MiniK15ControlLayout.Controls.Single(control => control.Id == id)).ToArray();
var digitKeys = ordinaryKeys.Where(control => control.Id.StartsWith("key-", StringComparison.Ordinal)).ToArray();
var modifierKeys = ordinaryKeys.Where(control => control.Id is "minus" or "plus").ToArray();
Require(digitKeys.Length == 11 && digitKeys.All(control => control.Kind == MiniK15ControlKind.SquareKey) &&
        modifierKeys.Length == 2 && modifierKeys.All(control => control.Kind == MiniK15ControlKind.RectangularKey) &&
        ordinaryKeys.All(control => control.Width == MiniK15ControlLayout.OrdinaryKeyWidth &&
            control.Height == MiniK15ControlLayout.OrdinaryKeyHeight),
    "DIGITS_DOT_SQUARE_MODIFIERS_RECTANGULAR");
Require(MiniK15ControlLayout.Controls.Single(control => control.Id == "enter") is
            { Kind: MiniK15ControlKind.WideEnter, Width: MiniK15ControlLayout.EnterWidth } &&
        MiniK15ControlLayout.Controls.Single(control => control.Id == "long-bottom") is
            { Kind: MiniK15ControlKind.LongBottomKey, Width: MiniK15ControlLayout.SpaceWidth } &&
        MiniK15ControlLayout.Controls.Single(control => control.Id == "rotary") is
            { Kind: MiniK15ControlKind.Rotary, Width: MiniK15ControlLayout.RotaryWidth } &&
        MiniK15ControlLayout.Controls.Single(control => control.Id == "joystick") is
            { Kind: MiniK15ControlKind.Joystick, Width: MiniK15ControlLayout.JoystickWidth } &&
        MiniK15ControlLayout.Controls.Count(control => control.Kind is MiniK15ControlKind.Rotary or
            MiniK15ControlKind.Joystick or MiniK15ControlKind.WideEnter or MiniK15ControlKind.LongBottomKey) == 4,
    "EXPLICIT_SPECIAL_CONTROL_KINDS");

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
    var squareBounds = controlBounds.Where(item => item.Control.Kind == MiniK15ControlKind.SquareKey).ToArray();
    var rectangularBounds = controlBounds.Where(item => item.Control.Kind is MiniK15ControlKind.RectangularKey or
        MiniK15ControlKind.WideEnter or MiniK15ControlKind.LongBottomKey).ToArray();
    Require(squareBounds.Length == 11 && squareBounds.All(item =>
            item.Bounds.Width == item.Bounds.Height &&
            Math.Abs((item.Bounds.Top + item.Bounds.Height / 2d) -
                (geometry.KeyboardBodyBounds.Top +
                 (item.Control.Y + item.Control.Height / 2d) * geometry.KeyboardBodyBounds.Height / 100d)) <= 1d) &&
        rectangularBounds.Length == 4 && rectangularBounds.All(item => item.Bounds.Width != item.Bounds.Height),
        $"PET_PIXEL_KEYCAP_SHAPES_AND_ALL_RECTANGLES_{preset}");
    var circleControls = controlBounds.Where(item => item.Control.Kind is MiniK15ControlKind.Rotary or
        MiniK15ControlKind.Joystick).ToArray();
    var circles = circleControls.Select(item => CodexPetControlVisualPolicy.CircleBounds(item.Bounds)).ToArray();
    Require(circles.Length == 2 && circles.All(circle => circle.Width == circle.Height) &&
        circles.Zip(circleControls, (circle, item) => Math.Abs((circle.Top + circle.Height / 2d) -
            (geometry.KeyboardBodyBounds.Top +
             (item.Control.Y + item.Control.Height / 2d) * geometry.KeyboardBodyBounds.Height / 100d)) <= 1d).All(aligned => aligned),
        $"PET_CIRCULAR_BOUNDS_AND_ROW_CENTER_ALIGNMENT_{preset}");
    var rings = circles.Select(circle =>
        (Circle: circle, Inner: CodexPetControlVisualPolicy.InnerRingBounds(circle))).ToArray();
    Require(rings.All(ring => ring.Circle.Left < ring.Inner.Left && ring.Inner.Top > ring.Circle.Top &&
            ring.Inner.Width == ring.Inner.Height &&
            ring.Inner.Left - ring.Circle.Left == ring.Circle.Right - ring.Inner.Right &&
            ring.Inner.Top - ring.Circle.Top == ring.Circle.Bottom - ring.Inner.Bottom) &&
        CodexPetControlVisualPolicy.RingInset(rings[0].Circle.Width) ==
            CodexPetControlVisualPolicy.RingInset(rings[1].Circle.Width),
        $"PET_CIRCLE_RING_INSETS_SYMMETRIC_{preset}");
    Require(controlBounds.SelectMany((left, index) => controlBounds.Skip(index + 1)
                .Select(right => (left, right)))
            .All(pair => !pair.left.Bounds.IntersectsWith(pair.right.Bounds)),
        $"PET_CONTROL_BOUNDS_DO_NOT_OVERLAP_{preset}");
    Require(controlBounds.Where(item => !string.IsNullOrWhiteSpace(item.Control.Label)).All(item =>
            CodexPetControlVisualPolicy.LabelFontSize(item.Control, item.Bounds) > 0 &&
            CodexPetControlVisualPolicy.LabelFontSize(item.Control, item.Bounds) <=
                Math.Max(1, item.Bounds.Height - 2) * (item.Control.Kind switch
                {
                    MiniK15ControlKind.SquareKey => 0.76f,
                    MiniK15ControlKind.RectangularKey => 0.60f,
                    MiniK15ControlKind.WideEnter => 0.54f,
                    MiniK15ControlKind.LongBottomKey => 0.52f,
                    _ => 0.5f
                }) + 0.01f &&
            CodexPetControlVisualPolicy.LabelFontSize(item.Control, item.Bounds) * item.Control.Label!.Length *
                (item.Control.Kind == MiniK15ControlKind.LongBottomKey ? 0.62f : 0.70f) <=
                Math.Max(1, item.Bounds.Width - 2) + 0.01f),
        $"PET_CONTROL_LABEL_FONTS_FIT_{preset}");
}
var orderedPresets = Enum.GetValues<PetSizePreset>();
var sampleSquare = MiniK15ControlLayout.Controls.Single(control => control.Id == "key-1");
var sampleModifier = MiniK15ControlLayout.Controls.Single(control => control.Id == "plus");
var squareFontSizes = orderedPresets.Select(preset => CodexPetControlVisualPolicy.LabelFontSize(sampleSquare,
    CodexPetSizePolicy.ControlBounds(CodexPetSizePolicy.Geometry(preset).KeyboardBodyBounds, sampleSquare))).ToArray();
var modifierFontSizes = orderedPresets.Select(preset => CodexPetControlVisualPolicy.LabelFontSize(sampleModifier,
    CodexPetSizePolicy.ControlBounds(CodexPetSizePolicy.Geometry(preset).KeyboardBodyBounds, sampleModifier))).ToArray();
Require(squareFontSizes.Zip(squareFontSizes.Skip(1), (a, b) => b > a).All(grows => grows) &&
        modifierFontSizes.Zip(modifierFontSizes.Skip(1), (a, b) => b > a).All(grows => grows),
    "PET_KEY_LABEL_FONT_POLICY_STRICTLY_GROWS_WITH_PRESET");
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

if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--render-proof")
        throw new ArgumentException("Usage: CodexPetWindowUxSmoke --render-proof <output-directory>");
    RenderVisualProof(Path.GetFullPath(args[1]));
}

static void RenderVisualProof(string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    var presets = Enum.GetValues<PetSizePreset>();
    var tileWidth = 644;
    var tileHeight = 348;
    using var montage = new Bitmap(tileWidth * 3, tileHeight * 2);
    using var montageGraphics = Graphics.FromImage(montage);
    montageGraphics.Clear(Color.FromArgb(22, 26, 32));
    montageGraphics.SmoothingMode = SmoothingMode.AntiAlias;
    using var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
    using var titleBrush = new SolidBrush(Color.Gainsboro);

    for (var index = 0; index < presets.Length; index++)
    {
        var preset = presets[index];
        var geometry = CodexPetSizePolicy.Geometry(preset);
        using var blue = RenderPreset(geometry, Color.Blue);
        using var red = RenderPreset(geometry, Color.Red);
        blue.Save(Path.Combine(outputDirectory, $"{preset}-blue.png"), System.Drawing.Imaging.ImageFormat.Png);
        red.Save(Path.Combine(outputDirectory, $"{preset}-red.png"), System.Drawing.Imaging.ImageFormat.Png);

        using var pair = new Bitmap(geometry.WindowSize.Width * 2, geometry.WindowSize.Height);
        using (var pairGraphics = Graphics.FromImage(pair))
        {
            pairGraphics.Clear(Color.FromArgb(22, 26, 32));
            pairGraphics.DrawImageUnscaled(blue, 0, 0);
            pairGraphics.DrawImageUnscaled(red, geometry.WindowSize.Width, 0);
        }
        pair.Save(Path.Combine(outputDirectory, $"{preset}.png"), System.Drawing.Imaging.ImageFormat.Png);

        var tileX = index % 3 * tileWidth;
        var tileY = index / 3 * tileHeight;
        montageGraphics.DrawString($"{preset}: BLUE | RED", titleFont, titleBrush, tileX + 8, tileY + 5);
        var pairScale = Math.Min(1d, Math.Min((tileWidth - 16d) / pair.Width, (tileHeight - 42d) / pair.Height));
        var drawWidth = (int)Math.Round(pair.Width * pairScale);
        var drawHeight = (int)Math.Round(pair.Height * pairScale);
        montageGraphics.DrawImage(pair, tileX + 8, tileY + 34, drawWidth, drawHeight);
    }

    montage.Save(Path.Combine(outputDirectory, "all-presets.png"), System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine($"VISUAL_PROOF={outputDirectory}");
}

static Bitmap RenderPreset(CodexPetSizeGeometry geometry, Color accent)
{
    var bitmap = new Bitmap(geometry.WindowSize.Width, geometry.WindowSize.Height);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.FromArgb(22, 26, 32));
    graphics.SmoothingMode = SmoothingMode.AntiAlias;

    var body = geometry.KeyboardBodyBounds;
    using var chassis = new SolidBrush(Color.FromArgb(40, 45, 54));
    using var outline = new Pen(Color.FromArgb(112, 125, 140), 2);
    using var bodyPath = RoundedPath(body, Math.Max(6, body.Width / 14));
    graphics.FillPath(chassis, bodyPath);
    graphics.DrawPath(outline, bodyPath);
    using var divider = new Pen(Color.FromArgb(92, 112, 126, 136), 1);
    graphics.DrawLine(divider, body.Left + 6, body.Top + body.Height * 35 / 100,
        body.Right - 6, body.Top + body.Height * 35 / 100);
    graphics.DrawLine(divider, body.Left + 6, body.Top + body.Height * 60 / 100,
        body.Right - 6, body.Top + body.Height * 60 / 100);

    var keyFill = Color.FromArgb(225,
        (int)Math.Round(accent.R + (255 - accent.R) * 0.18),
        (int)Math.Round(accent.G + (255 - accent.G) * 0.18),
        (int)Math.Round(accent.B + (255 - accent.B) * 0.18));
    foreach (var control in MiniK15ControlLayout.Controls)
        CodexPetControlRenderer.Draw(graphics, control,
            CodexPetSizePolicy.ControlBounds(body, control), keyFill);
    CodexPetBadgeRenderer.Draw(graphics, geometry.BadgeBounds, "1", accent);
    return bitmap;
}

static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
{
    var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
    var path = new GraphicsPath();
    path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
    path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
    path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}
