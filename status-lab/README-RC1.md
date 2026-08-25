# VOROTEX K15 Status Lab RC1

RC1 is a stabilization increment over the merged beta baseline.

Key changes:

- Codex `PreToolUse` is now captured so in-app approval can resume WAITING -> RUNNING as soon as the approved tool actually starts; `PostToolUse` and Windows notification removal remain fallback confirmations.
- DONE fallback default is 30 seconds.
- Tray has a manual WAITING/DONE acknowledgement action and a manual exact-baseline lighting recovery action.
- Status Lab profile-switch overlay is disabled by default because K15 already plays its native profile-switch flash.
- RGB activation uses fast two-color Cycle breathing so both profile colors are visible in the short signal.
- Deferred inactive-profile exact baselines are retained in memory after RGB OFF and restored when that physical profile is next active or by manual recovery. Status Lab still never programmatically selects A/B.
- Config schema v4 migrates exact beta defaults in memory and preserves customized values.
- Lighting Lab and offline configurator remain bundled.

See `owner-canary-7-rc1-plan.md` for the physical acceptance sequence.
