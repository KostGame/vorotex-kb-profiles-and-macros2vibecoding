namespace Vorotex.K15.StatusLab;

// Presentation-only description shared by independent renderers. It deliberately
// carries no device, profile, session, or renderer-specific identifiers.
internal enum VisualEffectFamily
{
    Constant,
    Flow,
    Breathing,
    CycleBreathing,
    Off
}

internal enum VisualEffectDirection { Forward, Reverse }

internal sealed record VisualEffectIntent(
    VisualEffectFamily Family,
    bool Enabled,
    double Tempo,
    VisualEffectDirection Direction,
    double Intensity,
    CodexPetVisualState State);

internal sealed record VisualEffectSample(double Intensity, double Tone);

internal static class VisualEffectIntentFactory
{
    internal static VisualEffectIntent ForPet(CodexPetVisualState state, StatusLabConfig config) => state switch
    {
        // Idle is intentionally neutral: NORMAL may depend on an observed physical
        // profile, while the screen companion must not.
        CodexPetVisualState.Idle => NeutralBaseline(state),
        CodexPetVisualState.Running => FromLighting(state, config.States.Running),
        CodexPetVisualState.Waiting => FromLighting(state, config.States.Waiting),
        CodexPetVisualState.Review => FromLighting(state, config.States.Done),
        _ => NeutralBaseline(CodexPetVisualState.Idle)
    };

    internal static VisualEffectIntent FromLighting(CodexPetVisualState state, LightingEffectConfig source)
    {
        if (!source.Enabled || source.Mode == K15LightingMode.Off)
            return new(VisualEffectFamily.Off, false, 0, VisualEffectDirection.Forward, 0, state);

        var family = source.Mode switch
        {
            K15LightingMode.Constant => VisualEffectFamily.Constant,
            K15LightingMode.FlowingWater => VisualEffectFamily.Flow,
            K15LightingMode.SingleColorBreathing => VisualEffectFamily.Breathing,
            K15LightingMode.CycleBreathing => VisualEffectFamily.CycleBreathing,
            _ => VisualEffectFamily.Off
        };
        if (family == VisualEffectFamily.Off)
            return new(VisualEffectFamily.Off, false, 0, VisualEffectDirection.Forward, 0, state);

        return new(
            family,
            true,
            Math.Clamp(source.Speed / 7d, 1d / 7d, 1d),
            source.Direction == 0 ? VisualEffectDirection.Forward : VisualEffectDirection.Reverse,
            Math.Clamp(source.Brightness / 6d, 0d, 1d),
            state);
    }

    private static VisualEffectIntent NeutralBaseline(CodexPetVisualState state) =>
        new(VisualEffectFamily.Constant, true, 0.5d, VisualEffectDirection.Forward, 0.7d, state);
}

// Pure animation math. The WinForms painter only turns this neutral sample into
// pixels, so motion can be tested without a window or a connected K15.
internal static class VisualEffectAnimator
{
    internal static VisualEffectSample Sample(VisualEffectIntent intent, double elapsedSeconds,
        int keyIndex, int keyCount)
    {
        if (!intent.Enabled || intent.Family == VisualEffectFamily.Off)
            return new(0, 0);

        var tempo = Math.Clamp(intent.Tempo, 1d / 7d, 1d);
        var baseIntensity = Math.Clamp(intent.Intensity, 0d, 1d);
        return intent.Family switch
        {
            VisualEffectFamily.Constant => new(baseIntensity, 0.5d),
            VisualEffectFamily.Flow => Flow(baseIntensity, tempo, intent.Direction, elapsedSeconds, keyIndex, keyCount),
            VisualEffectFamily.Breathing => new(baseIntensity * Pulse(tempo, elapsedSeconds), 0.5d),
            VisualEffectFamily.CycleBreathing => Cycle(baseIntensity, tempo, elapsedSeconds),
            _ => new(0, 0)
        };
    }

    private static VisualEffectSample Flow(double intensity, double tempo, VisualEffectDirection direction,
        double elapsedSeconds, int keyIndex, int keyCount)
    {
        var position = keyCount <= 1 ? 0.5d : Math.Clamp(keyIndex / (keyCount - 1d), 0d, 1d);
        if (direction == VisualEffectDirection.Reverse)
            position = 1d - position;
        var center = PositiveModulo(elapsedSeconds * tempo * 0.7d, 1.25d) - 0.125d;
        var distance = Math.Abs(position - center);
        var crest = Math.Clamp(1d - distance / 0.34d, 0d, 1d);
        return new(intensity * (0.22d + 0.78d * crest), 0.5d + 0.3d * crest);
    }

    private static VisualEffectSample Cycle(double intensity, double tempo, double elapsedSeconds)
    {
        var phase = (Math.Sin(elapsedSeconds * tempo * Math.PI * 2d) + 1d) / 2d;
        return new(intensity * (0.45d + 0.55d * phase), 0.2d + 0.6d * phase);
    }

    private static double Pulse(double tempo, double elapsedSeconds) =>
        0.3d + 0.7d * ((Math.Sin(elapsedSeconds * tempo * Math.PI * 2d) + 1d) / 2d);

    private static double PositiveModulo(double value, double divisor) =>
        ((value % divisor) + divisor) % divisor;
}
