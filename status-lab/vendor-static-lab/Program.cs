using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.VendorStaticLab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var exe = VendorPeAnalyzer.FindVendorExecutable();
        if (exe is null)
        {
            MessageBox.Show(
                "Не найден C:\\Program Files (x86)\\VOROTEX-K15-PRO\\VOROTEX-K15-PRO.exe",
                "VOROTEX K15 Vendor Static Lab",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var report = VendorPeAnalyzer.Analyze(exe);
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VOROTEX", "K15 Vendor Static Lab",
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);

            var jsonPath = Path.Combine(root, "vendor-static-report.json");
            var textPath = Path.Combine(root, "vendor-static-report.txt");
            File.WriteAllText(jsonPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(textPath, VendorPeAnalyzer.ToText(report), new UTF8Encoding(false));

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{jsonPath}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Report is already written. Explorer convenience is non-critical.
            }

            MessageBox.Show(
                $"Готово.\n\nHidD_SetFeature call-sites: {report.SetFeatureCallSites.Count}\n" +
                $"HidD_GetFeature call-sites: {report.GetFeatureCallSites.Count}\n" +
                $"Sleep/power string matches: {report.KeywordMatches.Count}\n\n" +
                "Пришли vendor-static-report.json. Сам VOROTEX-K15-PRO.exe присылать не нужно.",
                "VOROTEX K15 Vendor Static Lab",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Статический анализ не завершён:\n{ex.Message}",
                "VOROTEX K15 Vendor Static Lab",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
