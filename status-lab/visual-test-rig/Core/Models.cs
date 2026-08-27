using System.Text.Json.Serialization;

namespace Vorotex.K15.VisualTestRig;

public sealed record CameraDevice(string Id, string Name);

public sealed record CameraDiagnostics(
    string CameraName,
    string CameraDeviceId,
    string? FrameSourceId,
    string? SourceKind,
    string? StreamType,
    string? NativeSubtype,
    uint? Width,
    uint? Height,
    string? FrameRate,
    string? ReaderStartStatus,
    long FrameArrivedCallbacks,
    long FramesAcquired,
    long BitmapsProduced,
    long SoftwareBitmapFrames,
    long Direct3DSurfaceFrames,
    long SurfaceCopiesSucceeded,
    long SurfaceCopiesFailed,
    long FramesDroppedWhileProcessing,
    DateTimeOffset? LastFrameUtc,
    string? LastError)
{
    public static readonly CameraDiagnostics Empty = new("—", "—", null, null, null, null, null, null, null, null, 0, 0, 0, 0, 0, 0, 0, 0, null, null);
}

public sealed record NormalizedRoi(bool Enabled, double X, double Y, double Width, double Height)
{
    public static readonly NormalizedRoi Disabled = new(false, 0, 0, 0, 0);
    public NormalizedRoi Clamp()
    {
        if (!Enabled || !double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height)) return Disabled;
        var x = Math.Clamp(X, 0, 1); var y = Math.Clamp(Y, 0, 1);
        var w = Math.Clamp(Width, 0, 1 - x); var h = Math.Clamp(Height, 0, 1 - y);
        return w <= 0 || h <= 0 ? Disabled : new(true, x, y, w, h);
    }
    public Rectangle ToPixels(Size size)
    {
        var r = Clamp(); if (!r.Enabled || size.Width <= 0 || size.Height <= 0) return Rectangle.Empty;
        var x = (int)Math.Floor(r.X * size.Width); var y = (int)Math.Floor(r.Y * size.Height);
        var right = (int)Math.Ceiling((r.X + r.Width) * size.Width); var bottom = (int)Math.Ceiling((r.Y + r.Height) * size.Height);
        return Rectangle.FromLTRB(x, y, Math.Clamp(right, x + 1, size.Width), Math.Clamp(bottom, y + 1, size.Height));
    }
}

public sealed record RigSettings(int SchemaVersion, NormalizedRoi Roi)
{
    public static readonly RigSettings Default = new(1, NormalizedRoi.Disabled);
}

public sealed record CaptureMetadata(
    int SchemaVersion, string CaptureType, DateTimeOffset CapturedUtc, string CameraName,
    int FrameWidth, int FrameHeight, NormalizedRoi Roi, string LatestImage,
    string CaptureDirectory, int FrameCount);

public static class BurstPlan
{
    public const int MinSeconds = 1, MaxSeconds = 10, MinFramesPerSecond = 2, MaxFramesPerSecond = 20;
    public static bool IsValid(int seconds, int framesPerSecond) => seconds is >= MinSeconds and <= MaxSeconds && framesPerSecond is >= MinFramesPerSecond and <= MaxFramesPerSecond;
    public static IReadOnlyList<TimeSpan> Create(int seconds, int framesPerSecond)
    {
        if (!IsValid(seconds, framesPerSecond)) throw new ArgumentOutOfRangeException(nameof(seconds), "Burst must be 1–10 seconds and 2–20 sampled frames/sec.");
        return Enumerable.Range(0, checked(seconds * framesPerSecond)).Select(i => TimeSpan.FromSeconds((double)i / framesPerSecond)).ToArray();
    }
}
