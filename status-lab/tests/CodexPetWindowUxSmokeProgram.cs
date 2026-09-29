using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
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
Require(Enum.GetValues<PetSizePreset>().All(preset =>
    {
        var geometry = CodexPetSizePolicy.Geometry(preset);
        var window = new Rectangle(Point.Empty, geometry.WindowSize);
        return window.Contains(geometry.ProfileBadgeBounds) &&
               !geometry.ProfileBadgeBounds.IntersectsWith(geometry.BadgeBounds);
    }),
    "PROFILE_BADGE_BOUNDED_AND_SEPARATE_FROM_TASK_BADGE");
var feedbackNow = DateTimeOffset.UtcNow;
var dispatchFeedback = new CodexPetControlFeedback(
    "key-1", CodexPetControlFeedbackPhase.Dispatching, feedbackNow);
var dispatchRender = CodexPetControlFeedbackPolicy.RenderState(
    "key-1", "key-1", null, dispatchFeedback, feedbackNow.AddMilliseconds(80));
var pressedRender = CodexPetControlFeedbackPolicy.RenderState(
    "key-1", null, "key-1", CodexPetControlFeedback.None(feedbackNow), feedbackNow);
Require(dispatchRender.Hovered && !dispatchRender.Pressed &&
        dispatchRender.FeedbackPhase == CodexPetControlFeedbackPhase.Dispatching &&
        dispatchRender.Pulse is >= 0.5 and <= 1.01 &&
        pressedRender.Pressed &&
        pressedRender.FeedbackPhase == CodexPetControlFeedbackPhase.None,
    "PET_CONTROL_FEEDBACK_RENDER_PHASES");
var successFeedback = new CodexPetControlFeedback(
    "key-1", CodexPetControlFeedbackPhase.Success, feedbackNow);
var blockedFeedback = new CodexPetControlFeedback(
    "key-1", CodexPetControlFeedbackPhase.Blocked, feedbackNow);
Require(CodexPetControlFeedbackPolicy.Normalize(
            successFeedback, feedbackNow + CodexPetControlFeedbackPolicy.SuccessDuration -
                             TimeSpan.FromMilliseconds(1)).Phase ==
        CodexPetControlFeedbackPhase.Success &&
        CodexPetControlFeedbackPolicy.Normalize(
            successFeedback, feedbackNow + CodexPetControlFeedbackPolicy.SuccessDuration +
                             TimeSpan.FromMilliseconds(1)).Phase ==
        CodexPetControlFeedbackPhase.None &&
        CodexPetControlFeedbackPolicy.Normalize(
            blockedFeedback, feedbackNow + CodexPetControlFeedbackPolicy.BlockedDuration +
                             TimeSpan.FromMilliseconds(1)).Phase ==
        CodexPetControlFeedbackPhase.None,
    "PET_CONTROL_FEEDBACK_TERMINAL_TTL");
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
        digitKeys.All(control => control.Width == MiniK15ControlLayout.OrdinaryKeyWidth) &&
        modifierKeys.All(control => control.Width == MiniK15ControlLayout.ModifierKeyWidth) &&
        ordinaryKeys.All(control =>
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

foreach (var preset in Enum.GetValues<PetSizePreset>())
{
    var geometry = CodexPetSizePolicy.Geometry(preset);
    foreach (var control in MiniK15ControlLayout.Controls)
    {
        var bounds = CodexPetSizePolicy.ControlBounds(geometry.KeyboardBodyBounds, control);
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        Require(CodexPetControlHitTest.HitTest(geometry.KeyboardBodyBounds, center)?.Id == control.Id,
            $"PET_CONTROL_HIT_CENTER_{preset}_{control.Id}");
    }

    var rotary = MiniK15ControlLayout.Controls.Single(control => control.Id == "rotary");
    var rotaryBounds = CodexPetSizePolicy.ControlBounds(geometry.KeyboardBodyBounds, rotary);
    Require(CodexPetControlHitTest.HitTest(
                geometry.KeyboardBodyBounds,
                new Point(rotaryBounds.Left + 1, rotaryBounds.Top + 1))?.Id != "rotary",
        $"PET_ROTARY_CORNER_NOT_CLICKABLE_{preset}");
}
Require(CodexPetControlHitTest.HitTest(
            CodexPetSizePolicy.Geometry(PetSizePreset.Medium).KeyboardBodyBounds,
            Point.Empty) is null,
    "PET_OUTSIDE_CONTROLS_NOT_CLICKABLE");

var expectedBindingCells = new Dictionary<string, int>(StringComparer.Ordinal)
{
    ["key-6"] = 0, ["enter"] = 1, ["key-5"] = 8, ["key-dot"] = 9, ["minus"] = 10,
    ["key-4"] = 16, ["key-0"] = 17, ["plus"] = 18, ["key-3"] = 24, ["key-9"] = 25,
    ["long-bottom"] = 26, ["key-2"] = 32, ["key-8"] = 33, ["rotary"] = 34,
    ["key-1"] = 40, ["key-7"] = 41, ["joystick"] = 42
};
Require(K15LayoutAuthorityModel.BindingCells.Count == expectedBindingCells.Count &&
        expectedBindingCells.All(pair =>
            K15LayoutAuthorityModel.BindingCellForControl(pair.Key) == pair.Value) &&
        K15LayoutAuthorityModel.BindingCells.Keys.All(id =>
            MiniK15ControlLayout.Controls.Any(control => control.Id == id)),
    "K15_HARDWARE_BINDING_CELL_MAP");

var macroBinding = K15LayoutAuthorityModel.DecodeBindingCell(18, new byte[] { 0x0A, 0x00, 0x0E, 0x00 });
var nativeBinding = K15LayoutAuthorityModel.DecodeBindingCell(1, new byte[] { 0x02, 0x28, 0x00, 0x00 });
var profileBinding = K15LayoutAuthorityModel.DecodeBindingCell(34, new byte[] { 0x09, 0x03, 0x00, 0x00 });
Require(macroBinding.IsMacro && macroBinding.MacroMemorySlot == 14 &&
        nativeBinding.IsNative && nativeBinding.NativeUsage == 40 &&
        profileBinding.IsProfileLoop,
    "K15_HARDWARE_BINDING_DECODER");

var groupGuid = "11111111-1111-1111-1111-111111111111";
var macroGuid = "22222222-2222-2222-2222-222222222222";
var syntheticKeys = K15LayoutAuthorityModel.StorageFields.Values
    .ToDictionary(field => field, _ => 40, StringComparer.Ordinal);
syntheticKeys[K15LayoutAuthorityModel.StorageFieldForControl("key-1")] = 700;
syntheticKeys[K15LayoutAuthorityModel.StorageFieldForControl("rotary")] = 312;
var syntheticMacroBindings = K15LayoutAuthorityModel.StorageFields.Values
    .ToDictionary<string, string, object>(
        field => field,
        field => field == K15LayoutAuthorityModel.StorageFieldForControl("key-1")
            ? new { MemMacId = 7, grpGuid = groupGuid, macGuid = macroGuid }
            : new { MemMacId = 0, grpGuid = "", macGuid = "" },
        StringComparer.Ordinal);
var syntheticProfileJson = JsonSerializer.Serialize(new
{
    KBconfig = new { KBKey = syntheticKeys, KBKeyMacro = syntheticMacroBindings }
});
var syntheticMacroJson = JsonSerializer.Serialize(new
{
    MacroGrpInfo = new[]
    {
        new
        {
            GrpGuid = groupGuid,
            MacroInfo = new[]
            {
                new
                {
                    MacroGuid = macroGuid,
                    macData = new
                    {
                        num = 2,
                        macSta = new[] { 1, 2 },
                        macVal = new[] { 4, 4 },
                        macDly = new[] { 5, 7 }
                    }
                }
            }
        }
    }
});
var semantic = K15LocalLayoutSemanticModel.Parse(syntheticProfileJson, syntheticMacroJson, 1);
Require(semantic.Actions["key-1"].ExpectedBindingRaw.SequenceEqual(new byte[] { 0x0A, 0x00, 0x07, 0x00 }) &&
        semantic.Actions["key-1"].MacroPayload!.SequenceEqual(new byte[] { 0x04, 0x05, 0x04, 0x87 }) &&
        semantic.Actions["key-2"].ExpectedBindingRaw.SequenceEqual(new byte[] { 0x02, 0x28, 0x00, 0x00 }) &&
        semantic.Actions["rotary"].ExpectedBindingRaw.SequenceEqual(new byte[] { 0x09, 0x03, 0x00, 0x00 }),
    "K15_LOCAL_SEMANTIC_WIRE_ENCODING");

var syntheticHardwareBindings = semantic.Actions.ToDictionary(
    pair => pair.Key,
    pair => K15LayoutAuthorityModel.DecodeBindingCell(
        K15LayoutAuthorityModel.BindingCellForControl(pair.Key),
        pair.Value.ExpectedBindingRaw),
    StringComparer.Ordinal);
var syntheticHardware = new K15HardwareLayoutSnapshot(
    1,
    syntheticHardwareBindings,
    new Dictionary<byte, byte[]> { [7] = semantic.Actions["key-1"].MacroPayload! });
Require(K15LayoutAttestation.Compare(syntheticHardware, semantic).IsVerified,
    "K15_LAYOUT_ATTESTATION_READY_VERIFIED");
var staleHardwareBindings = new Dictionary<string, K15OnboardBindingCell>(syntheticHardwareBindings, StringComparer.Ordinal)
{
    ["key-1"] = K15LayoutAuthorityModel.DecodeBindingCell(40, new byte[] { 0x0A, 0x00, 0x08, 0x00 })
};
Require(K15LayoutAttestation.Compare(
        syntheticHardware with { Bindings = staleHardwareBindings }, semantic).State == K15LayoutVerificationState.Stale,
    "K15_LAYOUT_ATTESTATION_BINDING_MISMATCH_FAILS_CLOSED");

var macroPlan = K15DispatchPlanner.Create(semantic.Actions["key-1"]);
Require(macroPlan.Kind == K15DispatchPlanKind.Macro &&
        macroPlan.Steps.SequenceEqual(new[]
        {
            new K15DispatchStep(K15DispatchStepKind.KeyDown, 0x04),
            new K15DispatchStep(K15DispatchStepKind.Delay, DelayMilliseconds: 5),
            new K15DispatchStep(K15DispatchStepKind.KeyUp, 0x04),
            new K15DispatchStep(K15DispatchStepKind.Delay, DelayMilliseconds: 7)
        }),
    "K15_DISPATCH_PLAN_MACRO_EVENT_TIMING");
try
{
    K15DispatchPlanner.Create(semantic.Actions["key-1"] with
    {
        MacroPayload = new byte[] { 0xE0, 0x05 }
    });
    Require(false, "K15_DISPATCH_PLAN_UNBALANCED_MACRO_EXPECTED_FAILURE");
}
catch (InvalidDataException)
{
    Require(true, "K15_DISPATCH_PLAN_UNBALANCED_MACRO_FAILS_CLOSED");
}
var nativePlan = K15DispatchPlanner.Create(semantic.Actions["key-2"]);
var profilePlan = K15DispatchPlanner.Create(semantic.Actions["rotary"]);
Require(nativePlan.Kind == K15DispatchPlanKind.NativeTap &&
        nativePlan.Steps.SequenceEqual(new[]
        {
            new K15DispatchStep(K15DispatchStepKind.KeyDown, 0x28),
            new K15DispatchStep(K15DispatchStepKind.KeyUp, 0x28)
        }) &&
        profilePlan.Kind == K15DispatchPlanKind.ProfileSwitch &&
        profilePlan.Steps.Count == 0,
    "K15_DISPATCH_PLAN_NATIVE_AND_PROFILE_SWITCH");

var currentProfileUsages = new byte[]
{
    4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,27,28,29,
    30,31,35,40,43,44,51,53,54,55,56,224,225,226
};
Require(currentProfileUsages.All(usage => K15HidUsageMap.TryScanCode(usage, out _)) &&
        !K15HidUsageMap.TryScanCode(0x7F, out _),
    "K15_WINDOWS_HID_USAGE_ALLOWLIST");
Require(K15HidUsageMap.TryScanCode(0x04, out var aScan) && aScan.Code == 0x1E &&
        K15HidUsageMap.TryScanCode(0x28, out var enterScan) && enterScan.Code == 0x1C &&
        K15HidUsageMap.TryScanCode(0xE0, out var ctrlScan) && ctrlScan.Code == 0x1D,
    "K15_WINDOWS_SCAN_CODE_PHYSICAL_SEMANTICS");

var recordingInput = new RecordingScanCodeInput();
var recordingDelay = new RecordingDispatchDelay();
var executor = new K15WindowsInputExecutor(recordingInput, recordingDelay);
await executor.ExecuteAsync(macroPlan);
Require(recordingInput.Events.SequenceEqual(new[] { "DOWN:001E", "UP:001E" }) &&
        recordingDelay.Delays.SequenceEqual(new[] { 5, 7 }),
    "K15_WINDOWS_EXECUTOR_FAKE_MACRO_SEQUENCE");

var cleanupInput = new RecordingScanCodeInput();
var failingDelay = new RecordingDispatchDelay { ThrowOnCall = 1 };
var cleanupExecutor = new K15WindowsInputExecutor(cleanupInput, failingDelay);
var cleanupPlan = new K15DispatchPlan(
    K15DispatchPlanKind.Macro,
    new[]
    {
        new K15DispatchStep(K15DispatchStepKind.KeyDown, 0xE0),
        new K15DispatchStep(K15DispatchStepKind.Delay, DelayMilliseconds: 5)
    });
try
{
    await cleanupExecutor.ExecuteAsync(cleanupPlan);
    Require(false, "K15_WINDOWS_EXECUTOR_FAILURE_EXPECTED");
}
catch (InvalidOperationException)
{
    Require(cleanupInput.Events.SequenceEqual(new[] { "DOWN:001D", "UP:001D" }),
        "K15_WINDOWS_EXECUTOR_RELEASES_PRESSED_KEYS_ON_FAILURE");
}

var switchControl = new RecordingProfileSlotControl(1);
var switchResult = K15ProfileSwitchTransaction.Execute(
    switchControl,
    slot => slot == 0);
Require(switchResult == new K15ProfileSwitchResult(1, 0) &&
        switchControl.CurrentSlot == 0 &&
        switchControl.SelectedSlots.SequenceEqual(new byte[] { 0 }),
    "K15_PROFILE_SWITCH_TRANSACTION_EXACT_TARGET");

var rollbackControl = new RecordingProfileSlotControl(1);
try
{
    K15ProfileSwitchTransaction.Execute(
        rollbackControl,
        slot => slot == 1);
    Require(false, "K15_PROFILE_SWITCH_ROLLBACK_EXPECTED_FAILURE");
}
catch (InvalidOperationException)
{
    Require(rollbackControl.CurrentSlot == 1 &&
            rollbackControl.SelectedSlots.SequenceEqual(new byte[] { 0, 1 }),
        "K15_PROFILE_SWITCH_TRANSACTION_ROLLBACK_VERIFIED");
}

var asyncOrder = new List<string>();
var asyncSwitchControl = new RecordingProfileSlotControl(1);
var asyncSwitchResult = await K15ProfileSwitchTransaction.ExecuteAsync(
    asyncSwitchControl,
    slot =>
    {
        asyncOrder.Add($"attest:{slot}");
        return slot == 0;
    },
    async slot =>
    {
        asyncOrder.Add($"readback:{slot}");
        await Task.Yield();
    },
    expectedOriginalSlot: 1);
Require(asyncSwitchResult == new K15ProfileSwitchResult(1, 0) &&
        asyncOrder.SequenceEqual(new[] { "readback:0", "attest:0" }),
    "K15_PROFILE_SWITCH_READBACK_PRECEDES_ATTESTATION");

var asyncRollbackOrder = new List<string>();
var asyncRollbackControl = new RecordingProfileSlotControl(1);
try
{
    await K15ProfileSwitchTransaction.ExecuteAsync(
        asyncRollbackControl,
        slot =>
        {
            asyncRollbackOrder.Add($"attest:{slot}");
            return slot == 1;
        },
        async slot =>
        {
            asyncRollbackOrder.Add($"readback:{slot}");
            await Task.Yield();
        },
        expectedOriginalSlot: 1);
    Require(false, "K15_PROFILE_SWITCH_ASYNC_ROLLBACK_EXPECTED_FAILURE");
}
catch (InvalidOperationException)
{
    Require(asyncRollbackControl.CurrentSlot == 1 &&
            asyncRollbackOrder.SequenceEqual(new[]
            {
                "readback:0", "attest:0", "readback:1", "attest:1"
            }),
        "K15_PROFILE_SWITCH_ROLLBACK_UI_READBACK_ORDER");
}

var stalePreparedControl = new RecordingProfileSlotControl(0);
try
{
    await K15ProfileSwitchTransaction.ExecuteAsync(
        stalePreparedControl,
        _ => true,
        expectedOriginalSlot: 1);
    Require(false, "K15_PROFILE_SWITCH_STALE_PREPARED_SLOT_EXPECTED_FAILURE");
}
catch (InvalidOperationException)
{
    Require(stalePreparedControl.CurrentSlot == 0 &&
            stalePreparedControl.SelectedSlots.Count == 0,
        "K15_PROFILE_SWITCH_STALE_PREPARED_SLOT_FAILS_BEFORE_SWITCH");
}

var layoutTemp = Path.Combine(Path.GetTempPath(), "k15-layout-authority-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(layoutTemp);
try
{
    var profile0Path = Path.Combine(layoutTemp, "Profile0.json");
    var profile1Path = Path.Combine(layoutTemp, "Profile1.json");
    var macroConfigPath = Path.Combine(layoutTemp, "macroConfig.json");
    File.WriteAllText(profile0Path, syntheticProfileJson);
    File.WriteAllText(profile1Path, syntheticProfileJson);
    File.WriteAllText(macroConfigPath, syntheticMacroJson);
    var paths = new K15LayoutFilePaths(profile0Path, profile1Path, macroConfigPath);
    var localRead = K15LayoutFileAuthority.Read(paths, 1);
    Require(K15LayoutFileAuthority.Matches(paths, localRead.Stamp),
        "K15_LAYOUT_FILE_STAMP_MATCHES_UNCHANGED");
    File.AppendAllText(macroConfigPath, " ");
    Require(!K15LayoutFileAuthority.Matches(paths, localRead.Stamp),
        "K15_LAYOUT_FILE_STAMP_MUTATION_INVALIDATES");
}
finally
{
    Directory.Delete(layoutTemp, recursive: true);
}

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
            2 * MiniK15ControlLayout.ModifierKeyWidth - 2 * MiniK15ControlLayout.RowGutter &&
        bottomRow[2].Width > 4 * MiniK15ControlLayout.OrdinaryKeyWidth &&
        Gap(bottomRow[2], bottomRow[3]) == MiniK15ControlLayout.BottomJoystickGap &&
        bottomRow[3].X + bottomRow[3].Width ==
            MiniK15ControlLayout.ChassisWidth - MiniK15ControlLayout.BottomOuterMargin,
    "BOTTOM_LEFT_CLUSTER_AND_DEDICATED_RIGHT_CELL");
const int legacyOrdinaryKeyWidth = 10;
const int legacyModifierKeyWidth = 8;
const int legacyRotaryWidth = 14;
const int legacyJoystickWidth = 14;
const int legacyEnterWidth = 24;
const int legacyRowGutter = 2;
const double legacyTopToMiddleCenterGap = 29d;
const double legacyMiddleToBottomCenterGap = 28d;
static double RowCenter(IReadOnlyList<MiniK15Control> row) =>
    row.Select(control => control.Y + control.Height / 2d).Average();

Require(MiniK15ControlLayout.OrdinaryKeyWidth > legacyOrdinaryKeyWidth &&
        MiniK15ControlLayout.ModifierKeyWidth > legacyModifierKeyWidth &&
        MiniK15ControlLayout.RotaryWidth > legacyRotaryWidth &&
        MiniK15ControlLayout.JoystickWidth > legacyJoystickWidth &&
        MiniK15ControlLayout.EnterWidth > legacyEnterWidth,
    "PET_CONTROLS_LARGER_THAN_PRE_214_LIVE_BASELINE");
Require(MiniK15ControlLayout.RowGutter < legacyRowGutter &&
        RowCenter(middleRow) - RowCenter(topRow) < legacyTopToMiddleCenterGap &&
        RowCenter(bottomRow) - RowCenter(middleRow) < legacyMiddleToBottomCenterGap,
    "PET_ROW_SPACING_TIGHTER_THAN_PRE_214_LIVE_BASELINE");
Require(Math.Abs(MiniK15ControlLayout.RotaryWidth / (double)MiniK15ControlLayout.OrdinaryKeyWidth - 1.4) < 0.03 &&
        Math.Abs(MiniK15ControlLayout.JoystickWidth / (double)MiniK15ControlLayout.OrdinaryKeyWidth - 1.4) < 0.03 &&
        Math.Abs(MiniK15ControlLayout.EnterWidth / (double)MiniK15ControlLayout.OrdinaryKeyWidth - 2.4) < 0.03 &&
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
    var sharedKeycapHeights = rectangularBounds.Select(item => item.Bounds.Height)
        .Concat(squareBounds.Select(item => item.Bounds.Height)).Distinct().ToArray();
    var sharedKeycapHeight = CodexPetSizePolicy.KeycapPixelHeight(geometry.KeyboardBodyBounds);
    Require(squareBounds.Length == 11 && squareBounds.All(item =>
            item.Bounds.Width == item.Bounds.Height && Math.Abs(item.Bounds.Height - sharedKeycapHeight) <= 1 &&
            Math.Abs((item.Bounds.Top + item.Bounds.Height / 2d) -
                (geometry.KeyboardBodyBounds.Top +
                 (item.Control.Y + item.Control.Height / 2d) * geometry.KeyboardBodyBounds.Height / 100d)) <= 1d) &&
        rectangularBounds.Length == 4 && sharedKeycapHeights.Length == 1 &&
        sharedKeycapHeights[0] == sharedKeycapHeight && rectangularBounds.All(item =>
            item.Bounds.Height == sharedKeycapHeight && item.Bounds.Width != item.Bounds.Height) &&
        controlBounds.Where(item => item.Control.Id is "minus" or "plus")
            .All(item => item.Bounds.Width < sharedKeycapHeight) &&
        controlBounds.Where(item => item.Control.Id is "enter" or "long-bottom")
            .All(item => item.Bounds.Width > sharedKeycapHeight),
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
            CodexPetSizePolicy.ControlBounds(body, control), keyFill,
            new CodexPetControlRenderState(false, false,
                CodexPetControlFeedbackPhase.None, 1d));
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

internal sealed class RecordingScanCodeInput : IK15ScanCodeInput
{
    internal List<string> Events { get; } = new();

    public void KeyDown(K15WindowsScanCode scanCode) =>
        Events.Add($"DOWN:{scanCode.Code:X4}{(scanCode.Extended ? ":E" : string.Empty)}");

    public void KeyUp(K15WindowsScanCode scanCode) =>
        Events.Add($"UP:{scanCode.Code:X4}{(scanCode.Extended ? ":E" : string.Empty)}");
}

internal sealed class RecordingDispatchDelay : IK15DispatchDelay
{
    private int _calls;
    internal List<int> Delays { get; } = new();
    internal int? ThrowOnCall { get; init; }

    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls++;
        Delays.Add(milliseconds);
        if (ThrowOnCall == _calls)
            throw new InvalidOperationException("synthetic delay failure");
        return Task.CompletedTask;
    }
}
internal sealed class RecordingProfileSlotControl : IK15ProfileSlotControl
{
    internal RecordingProfileSlotControl(byte initialSlot) => CurrentSlot = initialSlot;

    internal byte CurrentSlot { get; private set; }
    internal List<byte> SelectedSlots { get; } = new();

    public byte ReadActiveSlot() => CurrentSlot;

    public void SelectActiveSlot(byte slot)
    {
        if (slot > 1) throw new ArgumentOutOfRangeException(nameof(slot));
        SelectedSlots.Add(slot);
        CurrentSlot = slot;
    }
}