# Windows Notification Engine M1 + Learning Lab M2 + Scheduler M3a

Parallel-safe foundation for using Windows toast notifications as temporary K15 display signals.

## Boundary

This subsystem is deliberately separate from the Codex semantic state machine:

- Codex hooks remain authoritative for `NORMAL / RUNNING / WAITING / DONE`.
- Arbitrary Windows notifications never create semantic `ERROR` or replace Codex state.
- M1/M2/M3a perform no K15/HID writes.
- Final display arbitration against Codex state and RGB rendering are later milestones.

## Pipeline

```text
UserNotificationListener
        |
        v
WindowsNotificationPoller
  Present / Added / Updated / Removed
        |
        +--> bounded Learning Buffer (RAM only)
        |          |
        |          v
        |   Notification Learning Lab
        |   inspect PFN/AUMID/title/body
        |   preview matched rule
        |   generate TOML draft
        |
        v
NotificationRuleEngine
        |
        v
NotificationOverlayIntent
        |
        v
NotificationOverlayScheduler
  active=1, pending<=1
        |
        X  no keyboard rendering yet
```

`Updated` is detected when the same Windows notification key has a different text fingerprint.

## Notification Learning Lab M2

`Vorotex.K15.NotificationLearningLab.exe` is a standalone observation tool.

It shows live Windows toast lifecycle events and, for the selected notification:

- application display name;
- Package Family Name;
- AppUserModelId;
- notification ID and fingerprint;
- title/body while the process is alive;
- the currently matching rule;
- the overlay intent that the M1 rule engine would produce.

The lab can generate a TOML rule draft from a selected notification. The default draft persists only a stable application identity, preferring:

1. `package_family_name`
2. `app_user_model_id`
3. `app_name`

Title/body are not persisted by default. An explicit checkbox may include the selected title in `title_contains`; body content is still omitted from the generated draft.

The generated block is copied to the clipboard for owner review. M2 does not silently modify `notifications.toml`.

## Bounded notification scheduler M3a

M3a adds pure scheduling logic before any display renderer exists.

Rules:

- exactly one active notification overlay;
- at most one pending overlay;
- a higher-priority overlay preempts the active overlay;
- the best still-valid interrupted/pending overlay may resume after the preempting overlay expires;
- same-notification `Updated` replaces the active overlay in place;
- removal/acknowledgement dismisses that notification and may promote pending work;
- equal-priority pending items coalesce to the newest one;
- pending items never grow into an unbounded historical queue;
- `pulse` lifetime uses `duration_seconds`;
- `while_present` and `until_acknowledged` remain bounded by `max_duration_seconds` as a safety fallback.

M3a still knows nothing about Codex semantic state. The later Display Arbiter will decide whether a scheduled notification overlay may visually interrupt `RUNNING`, `WAITING` or `DONE`.

## Privacy

The poller exposes toast `Title` and `Body` only to in-process observers for Learning Mode and rule matching. The existing event journal continues to persist metadata/fingerprint/lengths/classification only, not raw notification text.

The Learning Lab itself has no raw-sample persistence path. Closing it discards its bounded RAM learning buffer.

## Rule identity and matching

Optional `title_contains`, `body_contains`, and `regex` refine a rule. Empty catch-all rules are rejected. Invalid or timed-out regex evaluation fails closed.

## Visual policy

Rules may specify a notification-owned color independent from the Profile A/B semantic color model:

- `custom`: notification color only
- `custom_plus_profile`: notification color plus the active profile color, intended for a future two-color overlay renderer

M1/M2/M3a only produce, inspect and schedule intents. They do not write lighting state.

## Config

Canonical path:

`%LOCALAPPDATA%\VOROTEX\K15 Status Lab\notifications.toml`

Unknown applications are ignored by default. `notifications.example.toml` is the annotated template.
