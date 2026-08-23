# Windows dispatcher

The alpha dispatcher uses **AutoHotkey v2** and digit-only sentinels emitted by K15 macros.

## Start

1. Install AutoHotkey v2 from its official distribution.
2. Run `vibecoding-k15.ahk`.
3. Keep the script running while using the VIBECODING hardware profile.

No keyboard-layout switch is required for the semantic command text. The K15 types only digits; AutoHotkey recognizes the sentinel and replaces it with Unicode text.

## Current alpha behavior

- semantic command keys insert text but never submit automatically;
- `NEW_LINE` sends `Shift+Enter`;
- `SUBMIT` sends `Enter`;
- the sentinel prefix is `771337`;
- if the dispatcher is stopped, the raw sentinel remains visible, which is the intended fail-visible behavior.

## Known limitations

- `NEW_LINE` is not yet application-specific. The next revision will distinguish ChatGPT, Codex, IDEs, terminals, and other applications after their exact shortcuts are calibrated.
- the dispatcher cannot yet identify which physical keyboard produced a sentinel. Collision risk is instead reduced with an intentionally improbable numeric prefix.
- joystick click and encoder storage are still unresolved in the VOROTEX profile schema.

## Safety

Do not attach automatic submit to `PUBLISH`, `MERGE`, `CREATE`, `FIX`, or similar consequential actions. Keep semantic command insertion and submission as separate physical gestures.
