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

static NotificationOverlayIntent Intent(
    string key,
    NotificationPriority priority,
    NotificationBehavior behavior,
    DateTimeOffset sourceUtc,
    double displaySeconds = 6,
    double maxSeconds = 60,
    bool dismiss = false) =>
    new(
        key,
        (uint)Math.Abs(key.GetHashCode()),
        $"rule-{key}",
        priority,
        behavior,
        dismiss ? WindowsNotificationChangeKind.Removed : WindowsNotificationChangeKind.Added,
        dismiss,
        maxSeconds,
        new NotificationVisualConfig
        {
            Effect = "single_color_breathing",
            ColorMode = NotificationColorMode.Custom,
            Color = "#FFFFFF",
            Brightness = 6,
            Speed = 7,
            DurationSeconds = displaySeconds
        },
        sourceUtc);

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
Require(added.MaxDurationSeconds == important.MaxDurationSeconds, "Overlay intent must carry rule max duration for bounded scheduling.");

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

var safeDraft = NotificationRuleDraftBuilder.Build(Obs(WindowsNotificationChangeKind.Added));
Require(safeDraft.Contains("package_family_name = \"TelegramMessengerLLP.TelegramDesktop_test\"", StringComparison.Ordinal),
    "Learning draft must prefer stable PFN identity when available.");
Require(safeDraft.Contains("title_contains = []", StringComparison.Ordinal) &&
        !safeDraft.Contains("title_contains = [\"Kost\"]", StringComparison.Ordinal),
    "Privacy-safe draft must not persist notification title by default.");
Require(!safeDraft.Contains("body_contains = [\"hello\"]", StringComparison.Ordinal),
    "Learning draft must never persist notification body by default.");

var titleDraft = NotificationRuleDraftBuilder.Build(Obs(WindowsNotificationChangeKind.Added), includeTitleCondition: true);
Require(titleDraft.Contains("title_contains = [\"Kost\"]", StringComparison.Ordinal),
    "Explicit title-specific draft must persist selected title condition.");
Require(titleDraft.Contains("body_contains = []", StringComparison.Ordinal),
    "Title-specific draft must still avoid persisting body text.");

var aumidDraft = NotificationRuleDraftBuilder.Build(
    Obs(WindowsNotificationChangeKind.Added, app: "", pfn: "", aumid: "Contoso.App", title: "X"));
Require(aumidDraft.Contains("app_user_model_id = \"Contoso.App\"", StringComparison.Ordinal),
    "Draft builder must fall back to AUMID when PFN is unavailable.");

var noIdentityRejected = false;
try
{
    NotificationRuleDraftBuilder.Build(Obs(WindowsNotificationChangeKind.Added, app: "", pfn: "", aumid: ""));
}
catch (InvalidDataException)
{
    noIdentityRejected = true;
}
Require(noIdentityRejected, "Draft builder must fail closed when no stable application identity exists.");

var clock = DateTimeOffset.Parse("2026-08-25T13:00:00Z");
var scheduler = new NotificationOverlayScheduler();
var activeNormal = Intent("normal", NotificationPriority.Normal, NotificationBehavior.Pulse, clock, displaySeconds: 20);
var firstDecision = scheduler.Apply(activeNormal, clock);
Require(firstDecision?.Kind == NotificationOverlayDecisionKind.Show && scheduler.Active?.Intent.NotificationKey == "normal",
    "First overlay must become active.");

var lowPending = Intent("low", NotificationPriority.Low, NotificationBehavior.Pulse, clock.AddSeconds(1), displaySeconds: 20);
Require(scheduler.Apply(lowPending, clock.AddSeconds(1)) is null && scheduler.Pending?.Intent.NotificationKey == "low",
    "Lower-priority notification must wait in the single pending slot.");
Require(scheduler.PendingCount == 1, "Notification queue must remain bounded to one pending overlay.");

var high = Intent("high", NotificationPriority.High, NotificationBehavior.Pulse, clock.AddSeconds(2), displaySeconds: 2);
var preempt = scheduler.Apply(high, clock.AddSeconds(2));
Require(preempt?.Kind == NotificationOverlayDecisionKind.Replace && scheduler.Active?.Intent.NotificationKey == "high",
    "Higher-priority overlay must preempt the active overlay.");
Require(scheduler.Pending?.Intent.NotificationKey == "normal",
    "Interrupted higher-priority candidate must beat a lower-priority pending overlay.");

var resume = scheduler.Tick(clock.AddSeconds(5));
Require(resume?.Kind == NotificationOverlayDecisionKind.Replace && scheduler.Active?.Intent.NotificationKey == "normal",
    "Expired preempting pulse must resume still-valid pending overlay.");

var normalUpdate = Intent("normal", NotificationPriority.Normal, NotificationBehavior.Pulse, clock.AddSeconds(6), displaySeconds: 10);
var replaceSame = scheduler.Apply(normalUpdate, clock.AddSeconds(6));
Require(replaceSame?.Kind == NotificationOverlayDecisionKind.Replace && replaceSame.Reason == "same_notification_updated",
    "Same notification update must replace active overlay in place.");

var dismissNormal = Intent("normal", NotificationPriority.Normal, NotificationBehavior.WhilePresent,
    clock.AddSeconds(7), maxSeconds: 60, dismiss: true);
var dismissed = scheduler.Apply(dismissNormal, clock.AddSeconds(7));
Require(dismissed?.Kind == NotificationOverlayDecisionKind.Dismiss && scheduler.Active is null,
    "Removing active persistent notification must dismiss its overlay.");

var persistentScheduler = new NotificationOverlayScheduler();
var persistent = Intent("persistent", NotificationPriority.Normal, NotificationBehavior.UntilAcknowledged,
    clock, displaySeconds: 1, maxSeconds: 30);
persistentScheduler.Apply(persistent, clock);
Require(persistentScheduler.Tick(clock.AddSeconds(2)) is null,
    "Until-acknowledged overlay must ignore short display duration and use bounded max duration.");
var persistentTimeout = persistentScheduler.Tick(clock.AddSeconds(31));
Require(persistentTimeout?.Kind == NotificationOverlayDecisionKind.Dismiss && persistentScheduler.Active is null,
    "Persistent overlay must fail safe at max_duration_seconds.");

var bounded = new NotificationOverlayScheduler();
bounded.Apply(Intent("critical", NotificationPriority.Critical, NotificationBehavior.Pulse, clock, displaySeconds: 30), clock);
bounded.Apply(Intent("low-a", NotificationPriority.Low, NotificationBehavior.Pulse, clock.AddSeconds(1), displaySeconds: 30), clock.AddSeconds(1));
bounded.Apply(Intent("low-b", NotificationPriority.Low, NotificationBehavior.Pulse, clock.AddSeconds(2), displaySeconds: 30), clock.AddSeconds(2));
Require(bounded.PendingCount == 1 && bounded.Pending?.Intent.NotificationKey == "low-b",
    "Equal-priority pending overlays must coalesce to the newest sample without growing a queue.");

Console.WriteLine("Windows notification engine + Learning Lab models + bounded overlay scheduler tests: PASS");
