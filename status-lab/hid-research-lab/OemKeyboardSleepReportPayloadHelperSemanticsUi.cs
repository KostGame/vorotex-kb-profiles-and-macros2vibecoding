using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.HidResearchLab;

internal static class OemKeyboardSleepReportPayloadHelperSemanticsUi
{
    private const string ButtonName = "KeyboardSleepPayloadHelperSemanticsButton";

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

            var top = Math.Max(630, form.Controls.Cast<Control>().Where(x => x is Button).Select(x => x.Bottom + 8).DefaultIfEmpty(630).Max());
            foreach (var control in form.Controls.Cast<Control>().Where(x => x.Top >= top).ToArray())
                control.Top += 40;
            if (form.ClientSize.Height < top + 88)
                form.ClientSize = new Size(form.ClientSize.Width, top + 88);

            var button = new Button
            {
                Name = ButtonName,
                Text = "Run payload helper semantics trace",
                Location = new Point(24, top),
                Size = new Size(330, 32),
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
        const string title = "SleepTime Payload Helper Semantics Trace";
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
        if (status is not null) status.Text = "Tracing report+1 helper bodies and caller ABI semantics...";
        try
        {
            var report = await Task.Run(() => OemNdeviceAggregateCopyAnalyzer.AnalyzeKeyboardSleepPayloadHelperSemantics(exeA.Text, exeB.Text));
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vorotex.K15.StatusLab",
                "research",
                "oem-keyboard-sleep-payload-helper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);

            File.WriteAllText(
                Path.Combine(root, "oem-keyboard-sleep-payload-helper-semantics.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "oem-keyboard-sleep-payload-helper-semantics.txt"),
                OemNdeviceAggregateCopyAnalyzer.KeyboardSleepPayloadHelperSemanticsToText(report),
                new UTF8Encoding(false));

            if (status is not null) status.Text = $"PAYLOAD HELPER SEMANTICS: {report.Verdict}\n{root}";
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (status is not null) status.Text = "Payload helper semantics trace failed: " + ex.Message;
            MessageBox.Show(ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }
}
