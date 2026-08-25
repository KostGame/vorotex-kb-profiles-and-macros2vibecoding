using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var observation = new WindowsNotificationObservation(
    WindowsNotificationChangeKind.Added,
    "toast-designer",
    77,
    DateTimeOffset.Parse("2026-08-25T20:00:00Z"),
    "Telegram",
    "Telegram.Desktop",
    "TelegramMessengerLLP.TelegramDesktop_test",
    "fingerprint",
    "Build ready",
    "The deployment completed",
    2,
    [11, 24],
    "generic",
    false,
    true,
    false);

var options = new NotificationRuleDraftOptions
{
    RuleId = "telegram-build-ready",
    IncludeTitleCondition = true,
    Priority = NotificationPriority.Critical,
    Behavior = NotificationBehavior.UntilAcknowledged,
    MaxDurationSeconds = 45,
    Display = new NotificationVisualConfig
    {
        Effect = "cycle_breathing",
        ColorMode = NotificationColorMode.CustomPlusProfile,
        Color = "#27A7E7",
        Brightness = 5,
        Speed = 6,
        Direction = 1,
        DurationSeconds = 8
    }
};

var draft = NotificationRuleDraftBuilder.Build(observation, options);
Require(draft.Contains("id = \"telegram-build-ready\"", StringComparison.Ordinal), "Custom rule id was lost.");
Require(draft.Contains("priority = \"critical\"", StringComparison.Ordinal), "Critical priority was lost.");
Require(draft.Contains("behavior = \"until_acknowledged\"", StringComparison.Ordinal), "Behavior was lost.");
Require(draft.Contains("max_duration_seconds = 45", StringComparison.Ordinal), "Max duration was lost.");
Require(draft.Contains("title_contains = [\"Build ready\"]", StringComparison.Ordinal), "Explicit title condition was lost.");
Require(draft.Contains("body_contains = []", StringComparison.Ordinal), "Designer must never persist notification body.");
Require(draft.Contains("effect = \"cycle_breathing\"", StringComparison.Ordinal), "Effect was lost.");
Require(draft.Contains("color_mode = \"custom_plus_profile\"", StringComparison.Ordinal), "Color mode was lost.");
Require(draft.Contains("color = \"#27A7E7\"", StringComparison.Ordinal), "Custom color was lost.");
Require(draft.Contains("brightness = 5", StringComparison.Ordinal), "Brightness was lost.");
Require(draft.Contains("speed = 6", StringComparison.Ordinal), "Speed was lost.");
Require(draft.Contains("direction = 1", StringComparison.Ordinal), "Direction was lost.");
Require(draft.Contains("duration_seconds = 8", StringComparison.Ordinal), "Duration was lost.");

var invalidColorRejected = false;
try
{
    NotificationRuleDraftBuilder.Build(observation, new NotificationRuleDraftOptions
    {
        Display = new NotificationVisualConfig { Color = "blue" }
    });
}
catch (InvalidDataException)
{
    invalidColorRejected = true;
}
Require(invalidColorRejected, "Designer must fail closed on invalid HEX colors.");

var noBodyLeak = NotificationRuleDraftBuilder.Build(observation, new NotificationRuleDraftOptions());
Require(!noBodyLeak.Contains("The deployment completed", StringComparison.Ordinal),
    "Generated draft leaked notification body text.");

Console.WriteLine("Notification rule designer draft tests: PASS");
