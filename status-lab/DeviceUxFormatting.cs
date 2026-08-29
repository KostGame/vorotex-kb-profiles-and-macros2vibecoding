namespace Vorotex.K15.StatusLab;

internal static class DeviceUxFormatting
{
    public static string CandidateLabel(StatusTrayDeviceCandidate candidate) =>
        $"{DisplayProduct(candidate.ProductString)} · {candidate.VendorProduct} · {candidate.Usage} · " +
        $"report {candidate.FeatureReportLength} · {ShortCandidateId(candidate.CandidateId)} · {candidate.VerificationResult}";

    public static string ShortCandidateId(string candidateId)
    {
        var normalized = candidateId.Trim().ToUpperInvariant();
        return normalized.Length == 0 ? "#----" : $"#{normalized[..Math.Min(4, normalized.Length)]}";
    }

    public static string DisplayProduct(string? productString) =>
        string.IsNullOrWhiteSpace(productString) ? "Unknown HID" : productString.Trim();

    public static string? ResolveControlCenterPath(string baseDirectory, string executableName = "Vorotex.K15.ControlCenter.exe")
    {
        var colocated = Path.Combine(baseDirectory, executableName);
        if (File.Exists(colocated))
            return colocated;

        var parent = Directory.GetParent(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null)
            return null;

        var splitSibling = Path.Combine(parent.FullName, "control-center", executableName);
        return File.Exists(splitSibling) ? splitSibling : null;
    }
}
