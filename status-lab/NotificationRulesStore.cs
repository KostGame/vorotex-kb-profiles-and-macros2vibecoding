using System.Text;

namespace Vorotex.K15.StatusLab;

internal static class NotificationRulesStore
{
    public static string BackupPath => BackupPathFor(NotificationRulesConfig.FilePath);
    public static string PreRestoreBackupPath => PreRestoreBackupPathFor(NotificationRulesConfig.FilePath);

    public static void AddRule(NotificationRule rule) =>
        AddRuleToFile(NotificationRulesConfig.FilePath, rule);

    public static void RestoreBackup() =>
        RestoreBackupForFile(NotificationRulesConfig.FilePath);

    internal static void AddRuleToFile(string filePath, NotificationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        EnsureConfigExists(filePath);

        NotificationRulesConfig config;
        try
        {
            config = NotificationRulesToml.Parse(File.ReadAllText(filePath, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                "notifications.toml is invalid; refusing to overwrite it. Fix or restore the file first.", ex);
        }

        if (config.Rules.Any(existing => string.Equals(existing.Id, rule.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Rule id '{rule.Id}' already exists. Choose another id or edit the existing rule.");

        config.Rules.Add(CloneRule(rule));
        config.Validate();
        WriteValidated(filePath, config, createBackup: true);
    }

    internal static void RestoreBackupForFile(string filePath)
    {
        var backupPath = BackupPathFor(filePath);
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("No notifications.toml.bak backup exists.", backupPath);

        var backupText = File.ReadAllText(backupPath, Encoding.UTF8);
        var restored = NotificationRulesToml.Parse(backupText);
        restored.Validate();

        EnsureConfigExists(filePath);
        File.Copy(filePath, PreRestoreBackupPathFor(filePath), overwrite: true);
        WriteTextAtomically(filePath, backupText);
    }

    internal static string BackupPathFor(string filePath) => filePath + ".bak";
    internal static string PreRestoreBackupPathFor(string filePath) => filePath + ".pre-restore.bak";

    private static void EnsureConfigExists(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        if (!File.Exists(filePath))
            File.WriteAllText(filePath,
                NotificationRulesToml.Serialize(NotificationRulesConfig.CreateDefault()),
                new UTF8Encoding(false));
    }

    private static void WriteValidated(string filePath, NotificationRulesConfig config, bool createBackup)
    {
        config.Validate();
        var text = NotificationRulesToml.Serialize(config);
        _ = NotificationRulesToml.Parse(text);

        if (createBackup && File.Exists(filePath))
            File.Copy(filePath, BackupPathFor(filePath), overwrite: true);

        WriteTextAtomically(filePath, text);
    }

    private static void WriteTextAtomically(string destination, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(destination))
                File.Replace(temp, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, destination);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
            }
        }
    }

    private static NotificationRule CloneRule(NotificationRule source) => new()
    {
        Id = source.Id,
        Enabled = source.Enabled,
        Priority = source.Priority,
        Behavior = source.Behavior,
        MaxDurationSeconds = source.MaxDurationSeconds,
        Match = new NotificationRuleMatch
        {
            PackageFamilyName = source.Match.PackageFamilyName,
            AppUserModelId = source.Match.AppUserModelId,
            AppName = source.Match.AppName,
            TitleContains = source.Match.TitleContains.ToArray(),
            BodyContains = source.Match.BodyContains.ToArray(),
            Regex = source.Match.Regex
        },
        Display = new NotificationVisualConfig
        {
            Effect = source.Display.Effect,
            ColorMode = source.Display.ColorMode,
            Color = source.Display.Color,
            Brightness = source.Display.Brightness,
            Speed = source.Display.Speed,
            Direction = source.Display.Direction,
            DurationSeconds = source.Display.DurationSeconds
        }
    };
}
