namespace Vorotex.K15.StatusLab;

internal readonly record struct PetMotionSample(double OffsetX, double OffsetY, double RotationDegrees = 0d);

// Pure, monotonic-time presentation motion. It never changes Form.Location.
internal static class CodexPetMotion
{
    internal const double WaitingAlarmArmDelaySeconds = 0.7d;

    internal static PetMotionSample Sample(CodexPetVisualState state, double elapsedSinceEntrySeconds)
    {
        var t = Math.Max(0d, elapsedSinceEntrySeconds);
        return state switch
        {
            CodexPetVisualState.Waiting => Waiting(t),
            CodexPetVisualState.Review => Review(t),
            CodexPetVisualState.Running => Sway(t, 2.2d, 2.8d),
            _ => Sway(t, 1.8d, 3.4d)
        };
    }

    private static PetMotionSample Sway(double t, double amplitude, double period) =>
        new(amplitude * Math.Sin(2d * Math.PI * t / period),
            0.15d * Math.Sin(2d * Math.PI * t / period));

    private static PetMotionSample Review(double t)
    {
        const double bounceSeconds = 0.72d;
        if (t < bounceSeconds)
        {
            var progress = t / bounceSeconds;
            var envelope = Math.Sin(Math.PI * progress);
            return new(0.75d * Math.Sin(2d * Math.PI * progress), -3.0d * envelope);
        }
        return Sway(t - bounceSeconds, 1.8d, 3.4d);
    }

    private static PetMotionSample Waiting(double t)
    {
        if (t < WaitingAlarmArmDelaySeconds)
            return Sway(t, 1.8d, 3.4d);

        t -= WaitingAlarmArmDelaySeconds;
        const double cycle = 2.25d;
        const double active = 0.56d;
        var phase = t % cycle;
        if (phase >= active) return new(0d, 0d);
        var progress = phase / active;
        var envelope = Math.Sin(Math.PI * progress);
        return new(1.8d * envelope * Math.Sin(4d * Math.PI * progress), -3.4d * envelope);
    }
}
