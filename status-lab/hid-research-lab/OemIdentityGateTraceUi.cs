using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vorotex.K15.VendorStaticLab;

namespace Vorotex.K15.HidResearchLab;

internal static class OemIdentityGateTraceUi
{
    public static void Attach(Form host)
    {
        var button = new Button
        {
            Text = "OEM Identity Gate Trace",
            Size = new Size(220, 34),
            Location = new Point(Math.Max(24, host.ClientSize.Width - 244), 20),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 84, 160),
            ForeColor = Color.White,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 205);
        button.Click += (_, _) => new OemIdentityGateTraceForm().Show(host);
        host.Controls.Add(button);
        button.BringToFront();
    }
}

internal sealed class OemIdentityGateTraceForm : Form
{
    private readonly TextBox _exeA = InputBox();
    private readonly TextBox _exeB = InputBox();
    private readonly Label _status = new()
    {
        AutoSize = false,
        Size = new Size(810, 90),
        ForeColor = Color.FromArgb(190, 210, 235),
        Location = new Point(24, 260)
    };
    private string? _lastOutput;

    public OemIdentityGateTraceForm()
    {
        Text = "VOROTEX K15 · OEM Identity Gate Trace";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(860, 390);
        BackColor = Color.FromArgb(13, 17, 23);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);

        Controls.Add(new Label
        {
            Text = "OEM Identity Gate Trace",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 18f),
            ForeColor = Color.White,
            Location = new Point(24, 18)
        });
        Controls.Add(new Label
        {
            Text = "Read-only: Ndevice.json + HidD_GetProductString + direct static xref candidates. Никаких HID запросов.",
            AutoSize = false,
            Size = new Size(810, 44),
            ForeColor = Color.FromArgb(145, 175, 225),
            Location = new Point(27, 58)
        });

        Controls.Add(Label("A · VOROTEX", 24, 108));
        _exeA.Location = new Point(24, 132);
        _exeA.Text = VendorPeAnalyzer.FindVendorExecutable() ?? string.Empty;
        Controls.Add(_exeA);
        var browseA = ActionButton("Выбрать A…", 706, 128, 128);
        browseA.Click += (_, _) => Browse(_exeA, "Выберите VOROTEX-K15-PRO.exe");
        Controls.Add(browseA);

        Controls.Add(Label("B · SXS-W909", 24, 170));
        _exeB.Location = new Point(24, 194);
        _exeB.Text = OemDeviceIdentityDiffAnalyzer.FindSxsW909Executable() ?? string.Empty;
        Controls.Add(_exeB);
        var browseB = ActionButton("Выбрать B…", 706, 190, 128);
        browseB.Click += (_, _) => Browse(_exeB, "Выберите SXS-W909.exe");
        Controls.Add(browseB);

        var run = ActionButton("Run identity gate trace", 24, 230, 220);
        run.Click += async (_, _) => await RunAsync(run);
        Controls.Add(run);

        var open = ActionButton("Открыть результат", 258, 230, 180);
        open.Click += (_, _) => OpenOutput();
        Controls.Add(open);

        Controls.Add(new Label
        {
            Text = "Safety: no HID handle · no Set/GetFeature · no process attach · no patching/spoofing.",
            AutoSize = true,
            ForeColor = Color.FromArgb(145, 158, 180),
            Location = new Point(460, 238)
        });

        _status.Text = "Готово. Проверь два EXE и запускай trace.";
        Controls.Add(_status);
    }

    private async Task RunAsync(Button button)
    {
        if (!File.Exists(_exeA.Text))
        {
            MessageBox.Show("EXE A не найден.", "OEM Identity Gate Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!File.Exists(_exeB.Text))
        {
            MessageBox.Show("EXE B не найден.", "OEM Identity Gate Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        button.Enabled = false;
        _status.Text = "Строю model/product identity trace…";
        try
        {
            var report = await Task.Run(() => OemIdentityGateTraceAnalyzer.Analyze(_exeA.Text, _exeB.Text));
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vorotex.K15.StatusLab",
                "research",
                "oem-identity-gate-trace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "oem-identity-gate-trace.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "oem-identity-gate-trace.txt"),
                OemIdentityGateTraceAnalyzer.ToText(report),
                new UTF8Encoding(false));
            _lastOutput = root;
            _status.Text = $"VERDICT: {report.Verdict} · score={report.EvidenceScore}\n{root}";
            OpenOutput();
        }
        catch (Exception ex)
        {
            _status.Text = "Trace не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "OEM Identity Gate Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private void OpenOutput()
    {
        if (string.IsNullOrWhiteSpace(_lastOutput) || !Directory.Exists(_lastOutput))
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", _lastOutput) { UseShellExecute = true });
    }

    private static void Browse(TextBox target, string title)
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Windows executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (File.Exists(target.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(target.Text);
        if (dialog.ShowDialog() == DialogResult.OK)
            target.Text = dialog.FileName;
    }

    private static TextBox InputBox() => new()
    {
        Size = new Size(665, 28),
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(15, 20, 28),
        ForeColor = Color.Gainsboro
    };

    private static Label Label(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(145, 175, 225),
        Location = new Point(x, y)
    };

    private static Button ActionButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 32),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(27, 33, 43),
        ForeColor = Color.Gainsboro
    };
}
