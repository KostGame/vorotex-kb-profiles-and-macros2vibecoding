using System.Text.Json;
using Vorotex.K15.VisualTestRig;

var failures = new List<string>();
void Check(bool condition, string name) { if (!condition) failures.Add(name); else Console.WriteLine("PASS " + name); }
var roi = new NormalizedRoi(true, -.1, .2, 2, .9).Clamp();
Check(roi == new NormalizedRoi(true, 0, .2, 1, .8), "roi normalization and clamping");
Check(NormalizedRoi.Disabled.ToPixels(new Size(100, 50)).IsEmpty, "disabled roi has no pixels");
Check(!BurstPlan.IsValid(0, 5) && !BurstPlan.IsValid(5, 21) && BurstPlan.IsValid(10, 20), "bounded burst validation");
var schedule = BurstPlan.Create(2, 3); Check(schedule.Count == 6 && schedule.SequenceEqual(schedule.OrderBy(x => x)), "burst frame ordering");
Check(AppPaths.CaptureFolderName(new DateTimeOffset(2026, 8, 26, 12, 34, 56, 789, TimeSpan.Zero)) == "20260826-123456-789", "capture directory naming");
var sourceName = typeof(ManagedBgraBitmap).Assembly.GetManifestResourceNames().Single(name => name.EndsWith("Camera.WinRtCameraFrameSource.cs", StringComparison.Ordinal));
using (var sourceReader = new StreamReader(typeof(ManagedBgraBitmap).Assembly.GetManifestResourceStream(sourceName)!))
{
    var source = sourceReader.ReadToEnd();
    Check(new[] { "IMemoryBufferByteAccess", "GetBuffer(", "byte*", "unsafe" }.All(forbidden => !source.Contains(forbidden, StringComparison.Ordinal)), "unsafe pixel bridge static guard");
}
var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 };
using (var copied = ManagedBgraBitmap.Create(pixels, 2, 2))
{
    Check(copied.Width == 2 && copied.Height == 2 && copied.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb() && copied.GetPixel(1, 1).ToArgb() == Color.White.ToArgb(), "managed BGRA pixel copy");
}
var root = Path.Combine(Path.GetTempPath(), "k15-visual-rig-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var store = new SettingsStore(root); store.Save(new RigSettings(1, roi)); Check(store.Load().Roi == roi, "settings serialization");
    File.WriteAllText(Path.Combine(root, "settings.json"), "not json"); Check(store.Load() == RigSettings.Default, "corrupt settings fallback");
    using var frame = new Bitmap(8, 6); using var graphics = Graphics.FromImage(frame); graphics.Clear(Color.CornflowerBlue);
    var storage = new CaptureStorage(root); var metadata = storage.Save("single", "Synthetic camera", [frame], roi);
    var latestPath = Path.Combine(root, "latest", "latest.json"); var latest = JsonSerializer.Deserialize<CaptureMetadata>(File.ReadAllText(latestPath));
    Check(latest?.CaptureType == "single" && latest.FrameCount == 1 && File.Exists(latest.LatestImage), "latest metadata serialization and image");
    Check(File.Exists(Path.Combine(metadata.CaptureDirectory, "capture.json")), "capture metadata serialization");
    AtomicFile.Write(latestPath, "{\"schemaVersion\":1}"); Check(File.ReadAllText(latestPath) == "{\"schemaVersion\":1}", "atomic latest metadata replacement");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
if (failures.Count > 0) { Console.Error.WriteLine("FAIL " + string.Join(", ", failures)); return 1; }
Console.WriteLine("K15 Visual Test Rig deterministic smoke: PASS"); return 0;
