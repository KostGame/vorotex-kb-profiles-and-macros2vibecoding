namespace Vorotex.K15.SleepSweepLab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SleepSweepForm());
    }
}
