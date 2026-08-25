using System.Text;

namespace Vorotex.K15.StatusLab;

internal static class NotificationRulesStore
{
    public static string BackupPath => NotificationRulesConfig.FilePath + ".bak";
    public static string PreRestoreBackupPath => NotificationRulesConfig.FilePath + ".pre-restore.bak";

    public static void AddRule(NotificationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        NotificationRulesConfig.EnsureExists();

        var config = NotificationRulesConfig.LoadOrCreate();
        if (!string.IsNullOrWhiteSpace(config.LoadWarning))
            throw new InvalidDataException("notifications.toml is invalid; refusing to overwrite it. Fix or restore the file first.");

        if (config.Rules.Any(existing => string.Equals(existing.Id, rule.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Rule id '{rule.Id}' already exists. Choose another id or edit the existing rule.");

        config.Rules.Add(CloneRule(rule));
        config.Validate();
        WriteValidated(config, createBackup: true);
    }

    public static void RestoreBackup()
    {
        if (!File.Exists(BackupPath))
            throw new FileNotFoundException("No notifications.toml.bak backup exists.", BackupPath);

        var backupText = File.ReadAllText(BackupPath, Encoding.UTF8);
        var restored = NotificationRulesToml.Parse(backupText);
        restored.Validate();

        NotificationRulesConfig.EnsureExists();
        File.Copy(NotificationRulesConfig.FilePath, PreRestoreBackupPath, overwrite: true);
        WriteTextAtomically(NotificationRulesConfig.FilePath, backupText);
    }

    private static void WriteValidated(NotificationRulesConfig config, bool createBackup)
    {
        config.Validate();
        var text = NotificationRulesToml.Serialize(config);
        _ = NotificationRulesToml.Parse(text);

        if (createBackup && File.Exists(NotificationRulesConfig.FilePath))
            File.Copy(NotificationRulesConfig.FilePath, BackupPath, overwrite: true);

        WriteTextAtomically(NotificationRulesConfig.FilePath, text);
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
