namespace Vorotex.K15.StatusLab;

internal static class K15RgbBindingGuard
{
    public static bool IsCurrent(
        object? boundController,
        long? boundGeneration,
        bool connected,
        object? currentController,
        long currentGeneration) =>
        connected &&
        boundGeneration is long generation && generation == currentGeneration &&
        boundController is not null && ReferenceEquals(boundController, currentController);
}
