# Owner canary 7: RC1 approval, attention and lighting recovery

Date: 2026-08-25

Baseline: beta merge `b52dec62729d6ced79866124dfeae322ac762014`.

## Owner observations

- RUNNING Flowing Water is accepted.
- In-Codex Approve was intermittently not observed: Status Lab remained WAITING until the Windows notification changed. A second attempt worked, so this requires a stronger primary hook signal plus physical retest.
- DONE fallback to NORMAL works, but 15 seconds is too short for attention; requested default is 30 seconds.
- A manual tray escape hatch is required for stale WAITING/DONE after the owner has already reviewed the task.
- Early RGB disable previously could leave one physical profile with modified native lighting and require the OEM tool to repair it.
- Status Lab profile-switch animation is not useful because the K15 already plays its own native A/B transition flashes.
- Two-color Flowing Water activation often visibly reaches only RED in the short window; Cycle breathing with the explicit RED+BLUE palette is physically proven and better for RGB-ON indication.
- Lighting Lab and offline configurator remain part of the product.

## RC1 policy

- Add Codex `PreToolUse` hook. `PermissionRequest -> WAITING`; subsequent `PreToolUse` or `PostToolUse` for that real task session -> RUNNING.
- Keep Windows notification removal as supplemental fallback, not primary approval semantics.
- DONE fallback default = 30 seconds.
- Add tray action `✓ Сбросить WAITING / DONE` to acknowledge Status Lab attention state without deleting Windows system toasts.
- Add tray action to restore exact native lighting for the currently physically active profile.
- Disable Status Lab profile-switch overlay by default. Hardware profile selection remains observe-only.
- RGB ON default = fast Cycle breathing with `profile_pair` RED+BLUE.
- Preserve deferred exact-baseline snapshots in memory after RGB OFF. If an inactive profile could not be restored safely, restore it when that profile becomes physically active again or via manual restore. Never programmatically switch profiles.
- Config schema v4 migrates only exact beta defaults in memory; owner-customized values remain intact and source config is not silently rewritten.

## Required RC1 physical canary

1. Install/update Codex hooks from tray and fully restart Codex.
2. Trigger a permission request and click Approve inside Codex. Confirm WAITING changes to RUNNING from `PreToolUse` before the command finishes.
3. Trigger STOP. Confirm STOP signal, then DONE breathing for approximately 30 seconds if no notification resolution occurs, then NORMAL.
4. While WAITING or DONE, use `✓ Сбросить WAITING / DONE`; confirm immediate NORMAL.
5. Enable RGB indication. Confirm activation visibly alternates RED and BLUE via Cycle breathing.
6. Physically switch A/B. Confirm no extra Status Lab profile-switch overlay is played.
7. Disable RGB while one profile is active, revisit both profiles physically, and use manual native-lighting restore if needed. Confirm no persistent notifier effect requires the OEM application for repair.
8. Lighting Lab remains launchable and exact restore still works.

RC1 must not merge until CI passes and the owner accepts the physical canary.