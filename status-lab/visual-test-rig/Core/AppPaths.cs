namespace Vorotex.K15.VisualTestRig;

public static class AppPaths
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VOROTEX", "K15 Visual Test Rig");
    public static string CaptureFolderName(DateTimeOffset timestamp) => timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
}
