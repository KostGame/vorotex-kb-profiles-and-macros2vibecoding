namespace Vorotex.K15.StatusLab;

internal interface IK15ProfileSlotControl
{
    byte ReadActiveSlot();
    void SelectActiveSlot(byte slot);
}

internal sealed record K15ProfileSwitchResult(
    byte OriginalSlot,
    byte SelectedSlot);

internal static class K15ProfileSwitchTransaction
{
    internal static K15ProfileSwitchResult Execute(
        IK15ProfileSlotControl control,
        Func<byte, bool> attestSlot)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(attestSlot);

        var original = control.ReadActiveSlot();
        if (original > 1)
            throw new InvalidDataException($"Invalid K15 active slot {original}.");

        var target = (byte)(1 - original);
        try
        {
            control.SelectActiveSlot(target);
            var observed = control.ReadActiveSlot();
            if (observed != target)
                throw new InvalidOperationException(
                    $"K15 profile switch expected slot {target}, observed {observed}.");
            if (!attestSlot(target))
                throw new InvalidOperationException(
                    $"K15 profile switch selected slot {target}, but layout attestation failed.");

            return new K15ProfileSwitchResult(original, target);
        }
        catch (Exception primary)
        {
            try
            {
                control.SelectActiveSlot(original);
                var restored = control.ReadActiveSlot();
                if (restored != original)
                    throw new InvalidOperationException(
                        $"K15 rollback expected slot {original}, observed {restored}.");
                if (!attestSlot(original))
                    throw new InvalidOperationException(
                        $"K15 rollback restored slot {original}, but layout attestation failed.");
            }
            catch (Exception rollback)
            {
                throw new AggregateException(
                    "K15 profile switch failed and rollback could not be verified.",
                    primary,
                    rollback);
            }

            throw;
        }
    }
}
