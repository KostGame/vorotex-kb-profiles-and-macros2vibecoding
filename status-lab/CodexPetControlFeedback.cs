namespace Vorotex.K15.StatusLab;

internal enum CodexPetControlFeedbackPhase
{
    None,
    Dispatching,
    Switching,
    Reattesting,
    Success,
    Blocked
}

internal readonly record struct CodexPetControlFeedback(
    string? ControlId,
    CodexPetControlFeedbackPhase Phase,
    DateTimeOffset ChangedUtc,
    byte? ConfirmedSlot = null)
{
    internal static CodexPetControlFeedback None(DateTimeOffset now) =>
        new(null, CodexPetControlFeedbackPhase.None, now);
}

internal readonly record struct CodexPetControlRenderState(
    bool Hovered,
    bool Pressed,
    CodexPetControlFeedbackPhase FeedbackPhase,
    double Pulse);

internal static class CodexPetControlFeedbackPolicy
{
    internal static readonly TimeSpan SuccessDuration = TimeSpan.FromMilliseconds(320);
    internal static readonly TimeSpan BlockedDuration = TimeSpan.FromMilliseconds(620);

    internal static CodexPetControlFeedback Normalize(
        CodexPetControlFeedback feedback,
        DateTimeOffset now)
    {
        var lifetime = feedback.Phase switch
        {
            CodexPetControlFeedbackPhase.Success => SuccessDuration,
            CodexPetControlFeedbackPhase.Blocked => BlockedDuration,
            _ => Timeout.InfiniteTimeSpan
        };

        return lifetime != Timeout.InfiniteTimeSpan && now - feedback.ChangedUtc >= lifetime
            ? CodexPetControlFeedback.None(now)
            : feedback;
    }

    internal static CodexPetControlRenderState RenderState(
        string controlId,
        string? hoveredControlId,
        string? pressedControlId,
        CodexPetControlFeedback feedback,
        DateTimeOffset now)
    {
        feedback = Normalize(feedback, now);
        var phase = string.Equals(feedback.ControlId, controlId, StringComparison.Ordinal)
            ? feedback.Phase
            : CodexPetControlFeedbackPhase.None;
        var elapsed = Math.Max(0d, (now - feedback.ChangedUtc).TotalSeconds);
        var pulse = phase is CodexPetControlFeedbackPhase.Dispatching or
            CodexPetControlFeedbackPhase.Switching or
            CodexPetControlFeedbackPhase.Reattesting
            ? 0.55d + 0.45d * (0.5d + 0.5d * Math.Sin(elapsed * Math.PI * 5d))
            : 1d;

        return new(
            string.Equals(hoveredControlId, controlId, StringComparison.Ordinal),
            string.Equals(pressedControlId, controlId, StringComparison.Ordinal),
            phase,
            pulse);
    }
}
