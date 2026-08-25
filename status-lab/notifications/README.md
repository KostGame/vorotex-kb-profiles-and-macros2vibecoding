# Windows Notification Engine M1

Parallel-safe foundation for using Windows toast notifications as temporary K15 display signals.

## Boundary

This subsystem is deliberately separate from the Codex semantic state machine:

- Codex hooks remain authoritative for `NORMAL / RUNNING / WAITING / DONE`.
- Arbitrary Windows notifications never create semantic `ERROR` or replace Codex state.
- M1 performs no K15/HID writes.
- Display arbitration and RGB rendering are later milestones.

## M1 pipeline

```text
UserNotificationListener
        |
        v
WindowsNotificationPoller
  Present / Added / Updated / Removed
        |
        +--> bounded Learning Buffer (RAM only)
        |
        v
NotificationRuleEngine
        |
        v
NotificationOverlayIntent
        |
        X  M1 stops here
```

`Updated` is detected when the same Windows notification key has a different text fingerprint.

## Privacy

The poller exposes toast `Title` and `Body` only to in-process observers for Learning Mode and rule matching. The existing event journal continues to persist metadata/fingerprint/lengths/classification only, not raw notification text.

## Rule identity

Prefer stable Windows identity fields:

1. `package_family_name`
2. `app_user_model_id`
3. `app_name` as a human-friendly fallback

Optional `title_contains`, `body_contains`, and `regex` refine a rule. Empty catch-all rules are rejected.

## Visual policy

Rules may specify a notification-owned color independent from the Profile A/B semantic color model:

- `custom`: notification color only
- `custom_plus_profile`: notification color plus the active profile color, intended for a future two-color overlay renderer

M1 only produces the intent. It does not write lighting state.

## Busy/priority model for later Display Arbiter

Priority levels are already modeled as `low / normal / high / critical`. The later arbiter will decide whether a notification can interrupt a Codex visual and will keep the queue bounded.

## Config

Canonical future path:

`%LOCALAPPDATA%\VOROTEX\K15 Status Lab\notifications.toml`

Unknown applications are ignored by default. `notifications.example.toml` is the annotated template.
