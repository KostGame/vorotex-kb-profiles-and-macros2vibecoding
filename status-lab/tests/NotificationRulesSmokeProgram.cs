using Vorotex.K15.StatusLab;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static WindowsNotificationObservation Obs(
    WindowsNotificationChangeKind kind,
    string key = "toast-1",
    uint id = 1,
    string app = "Telegram",
    string aumid = "Telegram.Desktop",
    string pfn = "TelegramMessengerLLP.TelegramDesktop_test",
    string title = "Kost",
    string body = "hello") =>
    new(kind, key, id, DateTimeOffset.Parse("2026-08-25T12:00:00Z"), app, aumid, pfn,
        "fingerprint", title, body, 2, [title.Length, body.Length], "generic", false, false, false);

var telegram = new NotificationRule
{
    Id = "telegram",
    Priority = NotificationPriority.Normal,
    Behavior = NotificationBehavior.Pulse,
    Match = new NotificationRuleMatch { AppName = "Telegram" },
    Display = new NotificationVisualConfig
    {
        Effect = "single_color_breathing",
        ColorMode = NotificationColorMode.Custom,
        Color = "#27A7E7",
        DurationSeconds = 6
    }
};

var important = new NotificationRule
{
    Id = "important-kost",
    Priority = NotificationPriority.High,
    Behavior = NotificationBehavior.WhilePresent,
    Match = new NotificationRuleMatch
    {
        AppName = "Telegram",
        TitleContains = ["kost"]
    },
    Display = new NotificationVisualConfig
    {
        Effect = "cycle_breathing",
        ColorMode = NotificationColorMode.CustomPlusProfile,
        Color = "#FFB000",
        DurationSeconds = 10
    }
};

var engine = new NotificationRuleEngine([telegram, important]);
var added = engine.Evaluate(Obs(WindowsNotificationChangeKind.Added));
Require(added is not null && added.RuleId == "important-kost", "Higher-priority matching rule must win.");
Require(added!.Display.ColorMode == NotificationColorMode.CustomPlusProfile, "custom_plus_profile visual policy changed.");
Require(!added.Dismiss, "Added notification must create, not dismiss, an overlay intent.");

var updated = engine.Evaluate(Obs(WindowsNotificationChangeKind.Updated, body: "updated"));
Require(updated?.RuleId == "important-kost" && updated.ChangeKind == WindowsNotificationChangeKind.Updated,
    "Updated notification must be re-evaluated.");

Require(engine.Evaluate(Obs(WindowsNotificationChangeKind.Present)) is null,
    "Startup-present notifications must not flash overlays.");

var removed = engine.Evaluate(Obs(WindowsNotificationChangeKind.Removed));
Require(removed is not null && removed.Dismiss && removed.RuleId == "important-kost",
    "Removing while_present notification must emit dismiss intent.");

var pulseOnly = new NotificationRuleEngine([telegram]);
Require(pulseOnly.Evaluate(Obs(WindowsNotificationChangeKind.Removed)) is null,
    "Pulse rules must not emit dismiss intent on notification removal.");

var bodyRule = new NotificationRule
{
    Id = "body",
    Match = new NotificationRuleMatch
    {
        PackageFamilyName = "TelegramMessengerLLP.TelegramDesktop_test",
        BodyContains = ["deploy", "ready"],
        Regex = "hello|deploy"
    }
};
Require(NotificationRuleEngine.Matches(bodyRule.Match, Obs(WindowsNotificationChangeKind.Added, body: "Deploy complete")),
    "Identity + contains + regex matcher must accept matching toast.");
Require(!NotificationRuleEngine.Matches(bodyRule.Match, Obs(WindowsNotificationChangeKind.Added, body: "nothing relevant")),
    "Body matcher must reject unrelated toast.");

var buffer = new NotificationLearningBuffer(2);
buffer.Observe(Obs(WindowsNotificationChangeKind.Added, key: "a", id: 1, title: "A"));
buffer.Observe(Obs(WindowsNotificationChangeKind.Added, key: "b", id: 2, title: "B"));
buffer.Observe(Obs(WindowsNotificationChangeKind.Updated, key: "a", id: 1, title: "A2"));
Require(buffer.Count == 2, "Learning buffer must remain bounded.");
Require(buffer.Snapshot()[0].Title == "A2", "Updated notification must replace same-key learning sample and become newest.");
buffer.Observe(Obs(WindowsNotificationChangeKind.Added, key: "c", id: 3, title: "C"));
Require(buffer.Count == 2 && buffer.Snapshot().All(item => item.Key != "b"),
    "Learning buffer must evict oldest sample at capacity.");

var config = NotificationRulesConfig.CreateDefault();
config.Rules.Add(important);
config.Validate();
var toml = NotificationRulesToml.Serialize(config);
Require(toml.Contains("default_action = \"ignore\"", StringComparison.Ordinal),
    "Unknown notification default must remain ignore.");
Require(toml.Contains("color_mode = \"custom_plus_profile\"", StringComparison.Ordinal),
    "TOML must preserve custom_plus_profile mode.");
var roundTrip = NotificationRulesToml.Parse(toml);
Require(roundTrip.Rules.Count == 1 && roundTrip.Rules[0].Id == "important-kost",
    "Notification TOML round-trip lost rule.");
Require(roundTrip.Rules[0].Priority == NotificationPriority.High &&
        roundTrip.Rules[0].Behavior == NotificationBehavior.WhilePresent,
    "Notification TOML round-trip lost priority/behavior.");

var catchAllRejected = false;
try
{
    var unsafeConfig = NotificationRulesConfig.CreateDefault();
    unsafeConfig.Rules.Add(new NotificationRule { Id = "catch-all" });
    unsafeConfig.Validate();
}
catch (InvalidDataException)
{
    catchAllRejected = true;
}
Require(catchAllRejected, "Rule without any matcher must fail closed.");

var regexRejected = false;
try
{
    NotificationRulesToml.Parse("""
schema_version = 1
[notifications]
enabled = true
learning_buffer_size = 50
default_action = "ignore"
[[rules]]
id = "bad-regex"
[rules.match]
app_name = "Telegram"
regex = "("
""");
}
catch (InvalidDataException)
{
    regexRejected = true;
}
Require(regexRejected, "Invalid regex must fail config validation.");

Console.WriteLine("Windows notification rule engine + learning buffer tests: PASS");
