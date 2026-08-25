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
        Size = new Size(810, 105),
        ForeColor = Color.FromArgb(190, 210, 235),
        Location = new Point(24, 422)
    };
    private string? _lastOutput;

    public OemIdentityGateTraceForm()
    {
        Text = "VOROTEX K15 · OEM Identity Gate Trace";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(860, 550);
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
            Text = "Read-only: Ndevice.json + ProductString + static xrefs + bounded x86 data-flow. Никаких HID запросов.",
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

        var run = ActionButton("Run identity gate trace", 24, 230, 210);
        run.Click += async (_, _) => await RunIdentityAsync(run);
        Controls.Add(run);

        var compare = ActionButton("Run compare branch trace", 246, 230, 220);
        compare.BackColor = Color.FromArgb(34, 84, 160);
        compare.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 205);
        compare.Click += async (_, _) => await RunCompareAsync(compare);
        Controls.Add(compare);

        var guarded = ActionButton("Run guarded block trace", 24, 270, 210);
        guarded.BackColor = Color.FromArgb(54, 72, 145);
        guarded.FlatAppearance.BorderColor = Color.FromArgb(84, 106, 190);
        guarded.Click += async (_, _) => await RunGuardedAsync(guarded);
        Controls.Add(guarded);

        var semantic = ActionButton("Run semantic bridge trace", 24, 310, 210);
        semantic.BackColor = Color.FromArgb(62, 67, 138);
        semantic.FlatAppearance.BorderColor = Color.FromArgb(94, 102, 184);
        semantic.Click += async (_, _) => await RunSemanticBridgeAsync(semantic);
        Controls.Add(semantic);

        var open = ActionButton("Открыть результат", 246, 310, 170);
        open.Click += (_, _) => OpenOutput();
        Controls.Add(open);

        var provenance = ActionButton("Run field provenance trace", 24, 350, 220);
        provenance.BackColor = Color.FromArgb(69, 63, 130);
        provenance.FlatAppearance.BorderColor = Color.FromArgb(105, 96, 178);
        provenance.Click += async (_, _) => await RunFieldProvenanceAsync(provenance);
        Controls.Add(provenance);

        Controls.Add(new Label
        {
            Text = "Safety: static read-only · no HID handle · no process launch/attach/debug · no patching/spoofing.",
            AutoSize = true,
            ForeColor = Color.FromArgb(145, 158, 180),
            Location = new Point(24, 394)
        });

        _status.Text = "Готово. Field provenance trace идёт от aligned DevName/DevCmpStr parser branch к persistent member writes без выполнения OEM кода.";
        Controls.Add(_status);
    }

    private bool ValidateInputs(string title)
    {
        if (!File.Exists(_exeA.Text))
        {
            MessageBox.Show("EXE A не найден.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (!File.Exists(_exeB.Text))
        {
            MessageBox.Show("EXE B не найден.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private async Task RunIdentityAsync(Button button)
    {
        if (!ValidateInputs("OEM Identity Gate Trace")) return;
        button.Enabled = false;
        _status.Text = "Строю model/product identity trace…";
        try
        {
            var report = await Task.Run(() => OemIdentityGateTraceAnalyzer.Analyze(_exeA.Text, _exeB.Text));
            var root = ResearchRoot("oem-identity-gate-trace");
            File.WriteAllText(Path.Combine(root, "oem-identity-gate-trace.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "oem-identity-gate-trace.txt"), OemIdentityGateTraceAnalyzer.ToText(report), new UTF8Encoding(false));
            _lastOutput = root;
            _status.Text = $"IDENTITY VERDICT: {report.Verdict} · score={report.EvidenceScore}\n{root}";
            OpenOutput();
        }
        catch (Exception ex)
        {
            _status.Text = "Identity trace не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "OEM Identity Gate Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { button.Enabled = true; }
    }

    private async Task RunCompareAsync(Button button)
    {
        if (!ValidateInputs("OEM Product Compare Branch Trace")) return;
        button.Enabled = false;
        _status.Text = "Декодирую bounded x86 region и связываю ProductString → compare/helper → Jcc…";
        try
        {
            var report = await Task.Run(() => OemProductCompareBranchAnalyzer.Analyze(_exeA.Text, _exeB.Text));
            var root = ResearchRoot("oem-product-compare-branch");
            File.WriteAllText(Path.Combine(root, "oem-product-compare-branch.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "oem-product-compare-branch.txt"), OemProductCompareBranchAnalyzer.ToText(report), new UTF8Encoding(false));
            _lastOutput = root;
            _status.Text = $"COMPARE VERDICT: {report.Verdict}\n{root}";
            OpenOutput();
        }
        catch (Exception ex)
        {
            _status.Text = "Compare branch trace не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "OEM Product Compare Branch Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { button.Enabled = true; }
    }

    private async Task RunGuardedAsync(Button button)
    {
        if (!ValidateInputs("OEM DevCmpStr Guarded Block Trace")) return;
        button.Enabled = false;
        _status.Text = "Раскрываю полный DevCmpStr==1 guarded block и трассирую DevName → runtime member…";
        try
        {
            var report = await Task.Run(() => OemDevCmpGuardedBlockAnalyzer.Analyze(_exeA.Text, _exeB.Text));
            var root = ResearchRoot("oem-devcmp-guarded-block");
            File.WriteAllText(Path.Combine(root, "oem-devcmp-guarded-block.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "oem-devcmp-guarded-block.txt"), OemDevCmpGuardedBlockAnalyzer.ToText(report), new UTF8Encoding(false));
            _lastOutput = root;
            _status.Text = $"GUARDED VERDICT: {report.Verdict}\n{root}";
            OpenOutput();
        }
        catch (Exception ex)
        {
            _status.Text = "Guarded block trace не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "OEM DevCmpStr Guarded Block Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { button.Enabled = true; }
    }

    private async Task RunSemanticBridgeAsync(Button button)
    {
        if (!ValidateInputs("OEM Identity Semantic Bridge Trace")) return;
        button.Enabled = false;
        _status.Text = "Выравниваю field xrefs по инструкциям, разрешаю IAT/helper calls и трассирую boolean compare…";
        try
        {
            var report = await Task.Run(() => OemIdentitySemanticBridgeAnalyzer.Analyze(_exeA.Text, _exeB.Text));
            var root = ResearchRoot("oem-identity-semantic-bridge");
            File.WriteAllText(Path.Combine(root, "oem-identity-semantic-bridge.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "oem-identity-semantic-bridge.txt"), OemIdentitySemanticBridgeAnalyzer.ToText(report), new UTF8Encoding(false));
            _lastOutput = root;
            _status.Text = $"SEMANTIC BRIDGE VERDICT: {report.Verdict}\n{root}";
            OpenOutput();
        }
        catch (Exception ex)
        {
            _status.Text = "Semantic bridge trace не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "OEM Identity Semantic Bridge Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { button.Enabled = true; }
    }

    private async Task RunFieldProvenanceAsync(Button button)
    {
        if (!ValidateInputs("OEM Identity Field Provenance Trace")) return;
        button.Enabled = false;
        _status.Text = "Трассирую DevName/DevCmpStr match-path и persistent member writes…";
        try
        {
            var report = await Task.Run(() => OemIdentityFieldProvenanceAnalyzer.Analyze(_exeA.Text, _exeB.Text));
            var root = ResearchRoot("oem-identity-field-provenance");
            File.WriteAllText(Path.Combine(root, "oem-identity-field-provenance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "oem-identity-field-provenance.txt"), OemIdentityFieldProvenanceAnalyzer.ToText(report), new UTF8Encoding(false));
            _lastOutput = root;
            _status.Text = $"FIELD PROVENANCE VERDICT: {report.Verdict}\n{root}";
            OpenOutput();
        }
        catch (Exception ex)
        {
            _status.Text = "Field provenance trace не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, "OEM Identity Field Provenance Trace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { button.Enabled = true; }
    }

    private static string ResearchRoot(string prefix)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vorotex.K15.StatusLab", "research", prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        return root;
    }

    private void OpenOutput()
    {
        if (string.IsNullOrWhiteSpace(_lastOutput) || !Directory.Exists(_lastOutput)) return;
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
        if (File.Exists(target.Text)) dialog.InitialDirectory = Path.GetDirectoryName(target.Text);
        if (dialog.ShowDialog() == DialogResult.OK) target.Text = dialog.FileName;
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