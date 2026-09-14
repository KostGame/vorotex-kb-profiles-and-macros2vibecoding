namespace Vorotex.K15.StatusLab;

internal enum K15Profile { Unknown, A, B }
internal enum ProfileColorConfidence { Exact, Unknown }
internal enum ProfileColorSource { DeviceReadback, None }

internal readonly record struct ProfileColorHint(
    K15Profile Profile,
    ProfileColorConfidence Confidence,
    ProfileColorSource Source,
    DateTimeOffset ObservedUtc)
{
    internal static ProfileColorHint Unknown(DateTimeOffset now) =>
        new(K15Profile.Unknown, ProfileColorConfidence.Unknown, ProfileColorSource.None, now);

    internal bool IsExact => Confidence == ProfileColorConfidence.Exact &&
                              Source == ProfileColorSource.DeviceReadback &&
                              Profile is K15Profile.A or K15Profile.B;
}

internal readonly record struct PetRgbColor(byte R, byte G, byte B);

internal readonly record struct PetPalette(PetRgbColor Primary, bool IsNeutral)
{
    internal static PetPalette Neutral { get; } = new(new PetRgbColor(103, 139, 153), true);
}

internal static class PetPaletteResolver
{
    internal static readonly TimeSpan ProfileHintTtl = TimeSpan.FromMilliseconds(1800);

    internal static PetPalette Resolve(StatusLabConfig config, ProfileColorHint hint,
        DateTimeOffset now)
    {
        if (!hint.IsExact || now - hint.ObservedUtc > ProfileHintTtl)
            return PetPalette.Neutral;

        var color = hint.Profile switch
        {
            K15Profile.A => config.Profiles.A.Color,
            K15Profile.B => config.Profiles.B.Color,
            _ => null
        };
        if (color is null)
            return PetPalette.Neutral;
        var parsed = StatusLabConfig.ParseColor(color);
        return new(new PetRgbColor(parsed.R, parsed.G, parsed.B), false);
    }
}

internal sealed class ProfileColorHintTracker
{
    private ProfileColorHint _hint = ProfileColorHint.Unknown(DateTimeOffset.UtcNow);

    internal ProfileColorHint Current(DateTimeOffset now) =>
        _hint.IsExact && now - _hint.ObservedUtc <= PetPaletteResolver.ProfileHintTtl
            ? _hint
            : ProfileColorHint.Unknown(now);

    internal ProfileColorHint Observe(byte slot, DateTimeOffset observedUtc) =>
        _hint = slot switch
        {
            0 => new(K15Profile.A, ProfileColorConfidence.Exact, ProfileColorSource.DeviceReadback, observedUtc),
            1 => new(K15Profile.B, ProfileColorConfidence.Exact, ProfileColorSource.DeviceReadback, observedUtc),
            _ => ProfileColorHint.Unknown(observedUtc)
        };

    internal ProfileColorHint Invalidate(DateTimeOffset now) =>
        _hint = ProfileColorHint.Unknown(now);
}
