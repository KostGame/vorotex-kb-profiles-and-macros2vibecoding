namespace Vorotex.K15.HidResearchLab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new HidResearchForm());
    }
}
