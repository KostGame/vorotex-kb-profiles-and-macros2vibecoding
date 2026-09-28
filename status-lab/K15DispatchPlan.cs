namespace Vorotex.K15.StatusLab;

internal enum K15DispatchPlanKind
{
    NativeTap,
    Macro,
    ProfileSwitch
}

internal enum K15DispatchStepKind
{
    KeyDown,
    KeyUp,
    Delay
}

internal sealed record K15DispatchStep(
    K15DispatchStepKind Kind,
    byte HidUsage = 0,
    int DelayMilliseconds = 0);

internal sealed record K15DispatchPlan(
    K15DispatchPlanKind Kind,
    IReadOnlyList<K15DispatchStep> Steps);

internal static class K15DispatchPlanner
{
    internal static K15DispatchPlan Create(K15LocalSemanticAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.MacroMemorySlot is not null)
        {
            if (action.MacroPayload is null || action.MacroPayload.Length % 2 != 0)
                throw new InvalidDataException($"Macro payload for {action.ControlId} is invalid.");

            var steps = new List<K15DispatchStep>(action.MacroPayload.Length);
            for (var offset = 0; offset < action.MacroPayload.Length; offset += 2)
            {
                var usage = action.MacroPayload[offset];
                var encodedDelay = action.MacroPayload[offset + 1];
                var isKeyUp = (encodedDelay & 0x80) != 0;
                var delay = encodedDelay & 0x7F;

                if (!K15HidUsageMap.TryVirtualKey(usage, out _))
                    throw new InvalidDataException(
                        $"Macro {action.ControlId} contains unsupported HID usage 0x{usage:X2}.");

                steps.Add(new K15DispatchStep(
                    isKeyUp ? K15DispatchStepKind.KeyUp : K15DispatchStepKind.KeyDown,
                    usage));
                if (delay > 0)
                    steps.Add(new K15DispatchStep(K15DispatchStepKind.Delay, DelayMilliseconds: delay));
            }

            return new K15DispatchPlan(K15DispatchPlanKind.Macro, steps);
        }

        if (action.ExpectedBindingRaw.AsSpan().SequenceEqual(
                new byte[] { 0x09, 0x03, 0x00, 0x00 }))
            return new K15DispatchPlan(K15DispatchPlanKind.ProfileSwitch, Array.Empty<K15DispatchStep>());

        if (action.ExpectedBindingRaw.Length == K15LayoutAuthorityModel.BindingCellSize &&
            action.ExpectedBindingRaw[0] == 0x02 &&
            action.ExpectedBindingRaw[2] == 0x00 &&
            action.ExpectedBindingRaw[3] == 0x00)
        {
            var usage = action.ExpectedBindingRaw[1];
            if (!K15HidUsageMap.TryVirtualKey(usage, out _))
                throw new InvalidDataException(
                    $"Native action {action.ControlId} contains unsupported HID usage 0x{usage:X2}.");

            return new K15DispatchPlan(K15DispatchPlanKind.NativeTap,
            [
                new K15DispatchStep(K15DispatchStepKind.KeyDown, usage),
                new K15DispatchStep(K15DispatchStepKind.KeyUp, usage)
            ]);
        }

        throw new InvalidDataException(
            $"Control {action.ControlId} has no supported encoded dispatch family.");
    }
}
