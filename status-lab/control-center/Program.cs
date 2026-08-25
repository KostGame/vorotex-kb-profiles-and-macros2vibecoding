using System.Threading;

namespace Vorotex.K15.ControlCenter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Local\\Vorotex.K15.ControlCenter", out var created);
        if (!created)
        {
            MessageBox.Show("K15 Control Center уже запущен.", "VOROTEX K15 Control Center",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new ControlCenterForm());
    }
}
