using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vorotex.K15.StatusLab;

internal interface IK15ScanCodeInput
{
    void KeyDown(K15WindowsScanCode scanCode);
    void KeyUp(K15WindowsScanCode scanCode);
}

internal interface IK15DispatchDelay
{
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
}

internal sealed class K15TaskDispatchDelay : IK15DispatchDelay
{
    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(milliseconds, cancellationToken);
}

internal sealed class K15WindowsInputExecutor
{
    private readonly IK15ScanCodeInput _input;
    private readonly IK15DispatchDelay _delay;

    internal K15WindowsInputExecutor(
        IK15ScanCodeInput input,
        IK15DispatchDelay? delay = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _delay = delay ?? new K15TaskDispatchDelay();
    }

    internal async Task ExecuteAsync(
        K15DispatchPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Kind == K15DispatchPlanKind.ProfileSwitch)
            throw new InvalidOperationException(
                "Profile switching must use the explicit K15 hardware transaction, not Windows input.");

        var pressed = new List<K15WindowsScanCode>();
        try
        {
            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (step.Kind)
                {
                    case K15DispatchStepKind.KeyDown:
                    {
                        var scanCode = RequireScanCode(step.HidUsage);
                        _input.KeyDown(scanCode);
                        pressed.Add(scanCode);
                        break;
                    }
                    case K15DispatchStepKind.KeyUp:
                    {
                        var scanCode = RequireScanCode(step.HidUsage);
                        _input.KeyUp(scanCode);
                        RemoveLast(pressed, scanCode);
                        break;
                    }
                    case K15DispatchStepKind.Delay:
                        if (step.DelayMilliseconds < 0)
                            throw new InvalidDataException("Dispatch delay cannot be negative.");
                        if (step.DelayMilliseconds > 0)
                            await _delay.DelayAsync(step.DelayMilliseconds, cancellationToken)
                                .ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported dispatch step {step.Kind}.");
                }
            }
        }
        catch
        {
            ReleasePressedKeys(pressed);
            throw;
        }
    }

    private static K15WindowsScanCode RequireScanCode(byte usage)
    {
        if (!K15HidUsageMap.TryScanCode(usage, out var scanCode))
            throw new InvalidDataException($"Unsupported HID usage 0x{usage:X2}.");
        return scanCode;
    }

    private static void RemoveLast(List<K15WindowsScanCode> pressed, K15WindowsScanCode scanCode)
    {
        for (var index = pressed.Count - 1; index >= 0; index--)
        {
            if (pressed[index] != scanCode) continue;
            pressed.RemoveAt(index);
            return;
        }
    }

    private void ReleasePressedKeys(List<K15WindowsScanCode> pressed)
    {
        for (var index = pressed.Count - 1; index >= 0; index--)
        {
            try { _input.KeyUp(pressed[index]); }
            catch { }
        }
        pressed.Clear();
    }
}

internal sealed class K15SendInputScanCodeTransport : IK15ScanCodeInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint KeyEventExtendedKey = 0x0001;

    public void KeyDown(K15WindowsScanCode scanCode) => Send(scanCode, keyUp: false);
    public void KeyUp(K15WindowsScanCode scanCode) => Send(scanCode, keyUp: true);

    private static void Send(K15WindowsScanCode scanCode, bool keyUp)
    {
        var flags = KeyEventScanCode |
                    (keyUp ? KeyEventKeyUp : 0) |
                    (scanCode.Extended ? KeyEventExtendedKey : 0);
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = 0,
                    ScanCode = scanCode.Code,
                    Flags = flags
                }
            }
        };

        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int Dx;
        internal int Dy;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        internal uint Message;
        internal ushort ParamL;
        internal ushort ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] Input[] inputs,
        int inputSize);
}
