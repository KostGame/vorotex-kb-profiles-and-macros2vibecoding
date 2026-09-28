namespace Vorotex.K15.StatusLab;

internal sealed record K15HardwareLayoutSnapshot(
    byte ActiveSlot,
    IReadOnlyDictionary<string, K15OnboardBindingCell> Bindings,
    IReadOnlyDictionary<byte, byte[]> MacroPayloads);

internal static class K15LayoutAuthority
{
    internal static K15HardwareLayoutSnapshot Capture(K15HidLightingController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        var before = controller.ReadActiveSlot();
        var bindings = new Dictionary<string, K15OnboardBindingCell>(StringComparer.Ordinal);
        var macroSlots = new HashSet<byte>();

        foreach (var pair in K15LayoutAuthorityModel.BindingCells)
        {
            var raw = controller.ReadBindingCell(pair.Value);
            var binding = K15LayoutAuthorityModel.DecodeBindingCell(pair.Value, raw);
            bindings[pair.Key] = binding;

            if (binding.MacroMemorySlot is byte macroSlot)
                macroSlots.Add(macroSlot);
        }

        var macros = new Dictionary<byte, byte[]>();
        foreach (var macroSlot in macroSlots.Order())
            macros[macroSlot] = controller.ReadMacroPayload(macroSlot);

        var after = controller.ReadActiveSlot();
        if (after != before)
            throw new K15HidLightingController.K15ProfileChangedException(before, after);

        return new K15HardwareLayoutSnapshot(before, bindings, macros);
    }
}
