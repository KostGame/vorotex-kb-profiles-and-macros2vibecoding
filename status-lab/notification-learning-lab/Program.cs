namespace Vorotex.K15.NotificationLearningLab;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new NotificationLearningLabForm());
    }
}
