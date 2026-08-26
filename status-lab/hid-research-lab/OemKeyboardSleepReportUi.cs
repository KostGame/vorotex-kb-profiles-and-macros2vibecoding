using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.HidResearchLab;

internal static class OemKeyboardSleepReportUi
{
    private const string ButtonName = "KeyboardSleepReportTraceButton";

    [ModuleInitializer]
    internal static void Initialize()
    {
        Application.Idle += (_, _) => AttachToOpenTraceForms();
    }

    private static void AttachToOpenTraceForms()
    {
        foreach (var form in Application.OpenForms.OfType<OemIdentityGateTraceForm>().ToArray())
        {
            if (form.Controls.Find(ButtonName, true).Length > 0) continue;

            var textBoxes = form.Controls.OfType<TextBox>().OrderBy(x => x.Top).ToArray();
            if (textBoxes.Length < 2) continue;

            var top = Math.Max(510, form.Controls.Cast<Control>().Where(x => x is Button).Select(x => x.Bottom + 8).DefaultIfEmpty(510).Max());
            foreach (var control in form.Controls.Cast<Control>().Where(x => x.Top >= top).ToArray())
                control.Top += 40;
            if (form.ClientSize.Height < top + 88)
                form.ClientSize = new Size(form.ClientSize.Width, top + 88);

            var button = new Button
            {
                Name = ButtonName,
                Text = "Run keyboard SleepTime report trace",
                Location = new Point(24, top),
                Size = new Size(300, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(64, 76, 116),
                ForeColor = Color.Gainsboro
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(92, 111, 168);
            button.Click += async (_, _) => await RunAsync(form, textBoxes[0], textBoxes[1], button);
            form.Controls.Add(button);
            button.BringToFront();
        }
    }

    private static async Task RunAsync(Form form, TextBox exeA, TextBox exeB, Button button)
    {
        const string title = "Keyboard SleepTime -> SetFeature Report Trace";
        if (!File.Exists(exeA.Text))
        {
            MessageBox.Show("EXE A ne naiden.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!File.Exists(exeB.Text))
        {
            MessageBox.Show("EXE B ne naiden.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var status = form.Controls.OfType<Label>().OrderByDescending(x => x.Top).FirstOrDefault();
        button.Enabled = false;
        if (status is not null) status.Text = "Tracing keyboard SleepTime handlers and static SetFeature report construction...";
        try
        {
            var report = await Task.Run(() => OemNdeviceAggregateCopyAnalyzer.AnalyzeKeyboardSleepReport(exeA.Text, exeB.Text));
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vorotex.K15.StatusLab",
                "research",
                "oem-keyboard-sleep-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);

            File.WriteAllText(
                Path.Combine(root, "oem-keyboard-sleep-report-trace.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "oem-keyboard-sleep-report-trace.txt"),
                OemNdeviceAggregateCopyAnalyzer.KeyboardSleepReportToText(report),
                new UTF8Encoding(false));

            if (status is not null)
                status.Text = $"KEYBOARD SLEEP REPORT: {report.Verdict}\n{root}";
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (status is not null) status.Text = "Keyboard SleepTime report trace failed: " + ex.Message;
            MessageBox.Show(ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }
}
