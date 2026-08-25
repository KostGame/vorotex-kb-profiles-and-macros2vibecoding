using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.HidResearchLab;

internal static class OemIdentityObjectCommitUi
{
    private const string ButtonName = "ObjectCommitTraceButton";

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

            var existingBottom = form.Controls.Cast<Control>().Where(x => x.Top >= 390).ToArray();
            foreach (var control in existingBottom) control.Top += 40;
            form.ClientSize = new Size(form.ClientSize.Width, form.ClientSize.Height + 40);

            var button = new Button
            {
                Name = ButtonName,
                Text = "Run object commit trace",
                Location = new Point(24, 390),
                Size = new Size(220, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(77, 58, 124),
                ForeColor = Color.Gainsboro
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(118, 89, 171);
            button.Click += async (_, _) => await RunAsync(form, textBoxes[0], textBoxes[1], button);
            form.Controls.Add(button);
            button.BringToFront();
        }
    }

    private static async Task RunAsync(Form form, TextBox exeA, TextBox exeB, Button button)
    {
        const string title = "OEM Identity Object Commit Trace";
        if (!File.Exists(exeA.Text))
        {
            MessageBox.Show("EXE A не найден.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!File.Exists(exeB.Text))
        {
            MessageBox.Show("EXE B не найден.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var status = form.Controls.OfType<Label>().OrderByDescending(x => x.Top).FirstOrDefault();
        button.Enabled = false;
        if (status is not null) status.Text = "Трассирую field staging через parser join до runtime object commit…";
        try
        {
            var report = await Task.Run(() => OemIdentityObjectCommitAnalyzer.Analyze(exeA.Text, exeB.Text));
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vorotex.K15.StatusLab",
                "research",
                "oem-identity-object-commit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);

            File.WriteAllText(
                Path.Combine(root, "oem-identity-object-commit.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "oem-identity-object-commit.txt"),
                OemIdentityObjectCommitAnalyzer.ToText(report),
                new UTF8Encoding(false));

            if (status is not null) status.Text = $"OBJECT COMMIT VERDICT: {report.Verdict}\n{root}";
            Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (status is not null) status.Text = "Object commit trace не завершён: " + ex.Message;
            MessageBox.Show(ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            button.Enabled = true;
        }
    }
}