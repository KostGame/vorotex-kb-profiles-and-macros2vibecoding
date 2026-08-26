using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.HidResearchLab;

internal static class OemNdeviceAggregateCopyUi
{
    private const string ButtonName = "NdeviceAggregateCopyTraceButton";

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

            foreach (var control in form.Controls.Cast<Control>().Where(x => x.Top >= 430).ToArray())
                control.Top += 40;
            form.ClientSize = new Size(form.ClientSize.Width, form.ClientSize.Height + 40);

            var button = new Button
            {
                Name = ButtonName,
                Text = "Run Ndevice aggregate copy trace",
                Location = new Point(24, 430),
                Size = new Size(260, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(85, 55, 118),
                ForeColor = Color.Gainsboro
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(126, 86, 168);
            button.Click += async (_, _) => await RunAsync(form, textBoxes[0], textBoxes[1], button);
            form.Controls.Add(button);
            button.BringToFront();
        }
    }

    private static async Task RunAsync(Form form, TextBox exeA, TextBox exeB, Button button)
    {
        const string title = "Ndevice Aggregate Copy Trace";
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
        if (status is not null) status.Text = "Tracing local Ndevice aggregate and bounded copy helpers...";
        try
        {
            var report = await Task.Run(() => OemNdeviceAggregateCopyAnalyzer.Analyze(exeA.Text, exeB.Text));
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vorotex.K15.StatusLab",
                "research",
                "oem-ndevice-aggregate-copy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);

            File.WriteAllText(
                Path.Combine(root, "oem-ndevice-aggregate-copy.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "oem-ndevice-aggregate-copy.txt"),
                OemNdeviceAggregateCopyAnalyzer.ToText(report),
                new UTF8Encoding(false));

            if (status is not null) status.Text = $"NDEVICE AGGREGATE VERDICT: {report.Verdict}\n{root}";
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (status is not null) status.Text = "Ndevice aggregate trace failed: " + ex.Message;
            MessageBox.Show(ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }
}