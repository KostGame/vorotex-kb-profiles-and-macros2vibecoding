using System.Drawing.Imaging;
using System.Text.Json;

namespace Vorotex.K15.VisualTestRig;

public sealed class CaptureStorage(string dataRoot)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string? LatestCapturePath { get; private set; }
    public CaptureMetadata Save(string captureType, string cameraName, IReadOnlyList<Bitmap> frames, NormalizedRoi roi)
    {
        if (frames.Count == 0) throw new InvalidOperationException("No webcam frame is available.");
        var captured = DateTimeOffset.UtcNow;
        var directory = Path.Combine(dataRoot, "captures", AppPaths.CaptureFolderName(captured));
        Directory.CreateDirectory(directory);
        for (var i = 0; i < frames.Count; i++) frames[i].Save(Path.Combine(directory, $"frame-{i:000}.png"), ImageFormat.Png);
        var latestDirectory = Path.Combine(dataRoot, "latest"); Directory.CreateDirectory(latestDirectory);
        var latestImage = Path.Combine(latestDirectory, "latest.png");
        var imageTemp = latestImage + ".tmp";
        frames[^1].Save(imageTemp, ImageFormat.Png); File.Move(imageTemp, latestImage, true);
        var metadata = new CaptureMetadata(1, captureType, captured, cameraName, frames[^1].Width, frames[^1].Height, roi.Clamp(), latestImage, directory, frames.Count);
        AtomicFile.Write(Path.Combine(directory, "capture.json"), JsonSerializer.Serialize(metadata, Json));
        AtomicFile.Write(Path.Combine(latestDirectory, "latest.json"), JsonSerializer.Serialize(metadata, Json));
        LatestCapturePath = latestImage;
        return metadata;
    }
}
