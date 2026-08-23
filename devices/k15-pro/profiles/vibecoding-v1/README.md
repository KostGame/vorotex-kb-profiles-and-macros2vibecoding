# VIBECODING v1 multilingual profiles

This directory is the canonical declarative source for the language-independent K15 VIBECODING layout and its localized command packs.

## Files

- `semantic-map.json` — maps all 15 standard physical controls to stable semantic actions.
- `index.json` — registry of available locales and their support tier.
- `locales/*.json` — localized command packs.

## Design rule

The hardware mapping does not change when the natural language changes.

```text
physical key -> semantic action -> locale pack -> dispatcher output
```

Example:

```text
TOP_1 -> CHECK -> ru-RU -> Russian CHECK command
TOP_1 -> CHECK -> en-US -> English CHECK command
TOP_1 -> CHECK -> zh-CN -> Simplified Chinese CHECK command
```

This avoids dedicating one scarce onboard K15 profile to each human language.

## Delivery types

Each localized action has one of two delivery types:

- `text` — insert the localized Unicode command into the focused application;
- `dispatcher` — execute a non-text semantic operation such as `NEW_LINE` or `PUSH_TO_TALK`.

`text` means intended Unicode text. It does not require or imply raw HID keystroke replay.

## Safety

`MERGE`, `PUBLISH`, approval, rejection, and similar consequential semantics are intentionally phrased so that the macro prepares or requests the action rather than silently bypassing confirmation.

These files are not vendor `Profile0.json`, `Profile1.json`, or `macroConfig.json` snapshots. They are sanitized, repository-owned declarative inputs for a future generator/dispatcher.
