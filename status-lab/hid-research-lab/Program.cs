namespace Vorotex.K15.HidResearchLab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new HidResearchForm();
        OemIdentityGateTraceUi.Attach(form);
        Application.Run(form);
    }
}
