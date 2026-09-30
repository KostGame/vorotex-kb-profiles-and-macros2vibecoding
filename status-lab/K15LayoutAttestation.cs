namespace Vorotex.K15.StatusLab;

internal enum K15LayoutVerificationState
{
    ReadyVerified,
    ReadyActionVerified,
    Stale
}

internal sealed record K15LayoutAttestationResult(
    K15LayoutVerificationState State,
    IReadOnlyList<string> Differences)
{
    internal bool IsVerified => State == K15LayoutVerificationState.ReadyVerified;
    internal bool IsActionVerified => State == K15LayoutVerificationState.ReadyActionVerified;
}

internal static class K15LayoutAttestation
{
    internal static K15LayoutAttestationResult Compare(
        K15HardwareLayoutSnapshot hardware,
        K15LocalSemanticSnapshot local)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(local);

        var differences = new List<string>();
        if (hardware.ActiveSlot != local.Slot)
            differences.Add($"slot hardware={hardware.ActiveSlot} local={local.Slot}");

        foreach (var pair in local.Actions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var controlId = pair.Key;
            var expected = pair.Value;

            if (!hardware.Bindings.TryGetValue(controlId, out var actual))
            {
                differences.Add($"{controlId}: hardware binding missing");
                continue;
            }

            if (!actual.Raw.AsSpan().SequenceEqual(expected.ExpectedBindingRaw))
            {
                differences.Add(
                    $"{controlId}: binding hardware={Convert.ToHexString(actual.Raw)} " +
                    $"local={Convert.ToHexString(expected.ExpectedBindingRaw)}");
                continue;
            }

            if (expected.MacroMemorySlot is not byte macroSlot)
                continue;

            if (expected.MacroPayload is null)
            {
                differences.Add($"{controlId}: local macro payload missing");
                continue;
            }

            if (!hardware.MacroPayloads.TryGetValue(macroSlot, out var hardwarePayload))
            {
                differences.Add($"{controlId}: hardware macro payload {macroSlot} missing");
                continue;
            }

            if (!hardwarePayload.AsSpan().SequenceEqual(expected.MacroPayload))
            {
                differences.Add(
                    $"{controlId}: macro payload {macroSlot} differs " +
                    $"hardware={Convert.ToHexString(hardwarePayload)} " +
                    $"local={Convert.ToHexString(expected.MacroPayload)}");
            }
        }

        return new K15LayoutAttestationResult(
            differences.Count == 0
                ? K15LayoutVerificationState.ReadyVerified
                : K15LayoutVerificationState.Stale,
            differences);
    }

    internal static K15LayoutAttestationResult CompareBindingsOnly(
        K15HardwareLayoutSnapshot hardware,
        K15LocalSemanticSnapshot local)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(local);

        var differences = new List<string>();
        if (hardware.ActiveSlot != local.Slot)
            differences.Add($"slot hardware={hardware.ActiveSlot} local={local.Slot}");

        if (K15LayoutAuthorityModel.BindingCells.Count != local.Actions.Count ||
            K15LayoutAuthorityModel.BindingCells.Keys.Any(key => !local.Actions.ContainsKey(key)))
        {
            differences.Add("local profile does not contain all proven Mini-K15 bindings");
        }

        foreach (var controlId in K15LayoutAuthorityModel.BindingCells.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!local.Actions.TryGetValue(controlId, out var expected) ||
                !hardware.Bindings.TryGetValue(controlId, out var actual))
            {
                differences.Add($"{controlId}: binding missing");
                continue;
            }

            if (!actual.Raw.AsSpan().SequenceEqual(expected.ExpectedBindingRaw))
                differences.Add($"{controlId}: binding differs");
        }

        return new K15LayoutAttestationResult(
            differences.Count == 0
                ? K15LayoutVerificationState.ReadyActionVerified
                : K15LayoutVerificationState.Stale,
            differences);
    }
}
