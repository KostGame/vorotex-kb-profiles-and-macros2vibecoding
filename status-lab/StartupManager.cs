using Microsoft.Win32;

namespace Vorotex.K15.StatusLab;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VorotexK15StatusLab";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var current = key?.GetValue(ValueName) as string;
            return string.Equals(current, ExpectedCommand(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть HKCU Run для автозапуска.");

        if (enabled)
            key.SetValue(ValueName, ExpectedCommand(), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);

        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "status_lab",
            @event = "windows_autostart_changed",
            enabled
        });

        return IsEnabled() == enabled;
    }

    private static string ExpectedCommand() => $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"";
}
