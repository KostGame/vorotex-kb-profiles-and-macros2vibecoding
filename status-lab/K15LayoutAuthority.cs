namespace Vorotex.K15.StatusLab;

internal interface IK15LayoutReadControl : IK15ProfileSlotControl
{
    byte[] ReadBindingCell(int cellIndex);
    byte[] ReadMacroPayload(byte memorySlot);
}

internal sealed class K15LayoutCaptureProfileChangedException : InvalidOperationException
{
    internal K15LayoutCaptureProfileChangedException(byte previousSlot, byte currentSlot)
        : base($"K15 profile changed during layout capture: {previousSlot} -> {currentSlot}.")
    {
        PreviousSlot = previousSlot;
        CurrentSlot = currentSlot;
    }

    internal byte PreviousSlot { get; }
    internal byte CurrentSlot { get; }
}

internal static class K15LayoutAuthority
{
    internal static K15HardwareLayoutSnapshot Capture(IK15LayoutReadControl controller)
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
            throw new K15LayoutCaptureProfileChangedException(before, after);

        return new K15HardwareLayoutSnapshot(before, bindings, macros);
    }

    internal static async Task<K15HardwareLayoutSnapshot> CaptureCooperativelyAsync(
        IK15LayoutReadControl controller,
        Func<Task>? yieldAsync = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        yieldAsync ??= static () => Task.Delay(1);

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

            // HID stays on the caller/UI thread. The short delay yields the
            // WinForms message loop between complete, sequential requests.
            await yieldAsync();
        }

        var macros = new Dictionary<byte, byte[]>();
        foreach (var macroSlot in macroSlots.Order())
        {
            macros[macroSlot] = controller.ReadMacroPayload(macroSlot);
            await yieldAsync();
        }

        var after = controller.ReadActiveSlot();
        if (after != before)
            throw new K15LayoutCaptureProfileChangedException(before, after);

        return new K15HardwareLayoutSnapshot(before, bindings, macros);
    }
}
