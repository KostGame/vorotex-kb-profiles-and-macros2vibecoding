using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vorotex.K15.SleepSweepLab;
using Vorotex.K15.VendorStaticLab;

namespace Vorotex.K15.HidResearchLab;

internal sealed class HidResearchForm : Form
{
    private readonly Label _status = new()
    {
        AutoSize = false,
        ForeColor = Color.FromArgb(188, 207, 235),
        Size = new Size(760, 100)
    };

    public HidResearchForm()
    {
        Text = "VOROTEX K15 HID Research Lab · RC2";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 510);
        MinimumSize = new Size(790, 480);
        BackColor = Color.FromArgb(13, 17, 23);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);

        Controls.Add(new Label
        {
            Text = "VOROTEX K15 HID RESEARCH LAB",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20f),
            ForeColor = Color.White,
            Location = new Point(24, 20)
        });
        Controls.Add(new Label
        {
            Text = "Отдельный research-инструмент · рабочий Status Tray сюда не встроен",
            AutoSize = true,
            ForeColor = Color.FromArgb(145, 175, 225),
            Location = new Point(27, 61)
        });

        var sleepCard = Card("Sleep Sweep", 24, 100, 372, 215);
        sleepCard.Controls.Add(Body(
            "Файловый sweep 1→10 минут. Ничего не пишет в HID. Оставлен для повторных контрольных серий и forensic-сравнений.",
            18, 53, 330, 70));
        var sleep = ActionButton("Открыть Sleep Sweep 1→10", 18, 132, 285);
        sleep.Click += (_, _) => new SleepSweepForm().Show(this);
        sleepCard.Controls.Add(sleep);
        Controls.Add(sleepCard);

        var staticCard = Card("Vendor PE / HID static analysis", 420, 100, 372, 215);
        staticCard.Controls.Add(Body(
            "Разбирает установленный VOROTEX-K15-PRO.exe: PE imports, HidD_SetFeature/GetFeature call-sites, sleep/power strings и xrefs. Только чтение файлов.",
            18, 53, 330, 80));
        var analyze = ActionButton("Анализировать штатный VOROTEX", 18, 140, 285);
        analyze.Click += async (_, _) => await AnalyzeVendorAsync(analyze);
        staticCard.Controls.Add(analyze);
        Controls.Add(staticCard);

        _status.Location = new Point(28, 342);
        _status.Text = "Следующий приоритет: vendor static report. Если xrefs окажутся недостаточными, сюда же добавим controlled HID capture, не создавая пятое приложение.";
        Controls.Add(_status);

        Controls.Add(new Label
        {
            Text = "Safety: неизвестные HID write-команды запрещены. Исследователь не меняет firmware, keys/macros, power или профили.",
            Location = new Point(28, 452),
            Size = new Size(755, 30),
            ForeColor = Color.FromArgb(145, 158, 180)
        });
    }

    private async Task AnalyzeVendorAsync(Button button)
    {
        button.Enabled = false;
        _status.Text = "Статический анализ VOROTEX-K15-PRO.exe...";
        try
        {
            var exe = VendorPeAnalyzer.FindVendorExecutable();
            if (exe is null)
                throw new FileNotFoundException("VOROTEX-K15-PRO.exe не найден в Program Files.");

            var report = await Task.Run(() => VendorPeAnalyzer.Analyze(exe));
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VOROTEX", "K15 HID Research Lab", "vendor-static",
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);

            var jsonPath = Path.Combine(root, "vendor-static-report.json");
            var textPath = Path.Combine(root, "vendor-static-report.txt");
            File.WriteAllText(jsonPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(textPath, VendorPeAnalyzer.ToText(report), new UTF8Encoding(false));

            _status.Text =
                $"Готово. HidD_SetFeature sites: {report.SetFeatureCallSites.Count}; " +
                $"GetFeature sites: {report.GetFeatureCallSites.Count}; sleep/power matches: {report.KeywordMatches.Count}.\n" +
                $"Пришли vendor-static-report.json: {jsonPath}";

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{jsonPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status.Text = "Анализ не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "VOROTEX K15 HID Research Lab",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private static Panel Card(string title, int x, int y, int width, int height)
    {
        var panel = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(width, height),
            BackColor = Color.FromArgb(22, 27, 34)
        };
        panel.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Color.White,
            Location = new Point(18, 17)
        });
        return panel;
    }

    private static Label Body(string text, int x, int y, int width, int height) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, height),
        ForeColor = Color.FromArgb(188, 200, 218)
    };

    private static Button ActionButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 42),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 47, 67),
        ForeColor = Color.White,
        Cursor = Cursors.Hand
    };
}
