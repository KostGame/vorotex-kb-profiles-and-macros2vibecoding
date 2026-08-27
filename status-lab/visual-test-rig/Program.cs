namespace Vorotex.K15.VisualTestRig;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new VisualTestRigForm(new WinRtCameraFrameSource(), new CaptureStorage(AppPaths.DataRoot)));
    }
}
