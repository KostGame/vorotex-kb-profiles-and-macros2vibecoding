# Production activation kit

This kit is an owner-run, reversible activation boundary for ordinary Codex Desktop. It does not patch WindowsApps, inject into a process, inspect handles/memory, or modify Machine environment. The checked-in files are activation tooling and a manifest template; this task does not arm the owner's machine.

## Architecture

The owner keeps a reviewed bundle at a stable per-user path:

`ordinary Codex Desktop -> User CODEX_CLI_PATH -> K15.CodexBridge.WindowsAdapter.exe -> approval-wrapper.mjs -> exact codex.exe`

`Activate-CodexBridge.ps1` validates the exact non-reparse adapter, Node, approval wrapper, transparent wrapper, bridge core and child paths plus a SHA-256 pin for every file before any environment change. It snapshots all bridge-owned User variables atomically to `activation-state.json`, then sets only User-level variables. Machine environment and package files remain untouched.

For every managed variable, the activation state records an explicit `presence` (`ABSENT` or `PRESENT`) and the exact string `value`; `PRESENT` with `""` is not collapsed to `ABSENT`. The real production primitive reads the current user's `HKCU\Environment` value-name set before reading its string, so it does not infer presence from `GetEnvironmentVariable`. It also preserves `String` versus `ExpandString` when restoring a present baseline.

The optional approval sink is configured explicitly in the manifest. Empty means no side-channel file is created. The adapter and wrappers continue to forward transport independently of observer/sink failure.

## Owner operations

Run `Validate` after reviewing/copying a bundle and before `Enable`:

```powershell
pwsh -NoProfile -File .\Activate-CodexBridge.ps1 -Mode Validate -ManifestPath .\manifest.json
pwsh -NoProfile -File .\Activate-CodexBridge.ps1 -Mode Enable -ManifestPath .\manifest.json
pwsh -NoProfile -File .\Activate-CodexBridge.ps1 -Mode Status -ManifestPath .\manifest.json
pwsh -NoProfile -File .\Activate-CodexBridge.ps1 -Mode Disable -ManifestPath .\manifest.json
```

Codex Desktop must be closed before `Enable` or `Disable`: the script checks both the `codex.exe` backend and the `ChatGPT.exe` Desktop UI and refuses to mutate the real User environment while either is running. This is intentionally conservative: any `ChatGPT.exe` blocks activation, including a regular ChatGPT Desktop instance, because an ambiguous UI identity must fail closed. Isolated tests may inject a process inventory; production `HKCU\Environment` always performs real process inspection, and the script never kills processes automatically. Launch Codex only after the successful operation so a newly launched process receives the changed User environment.

After active writes, `Enable` independently rereads all six variables and requires the exact active presence/value state before reporting `ACTIVE=YES`. `Disable` restores and independently rereads the recorded presence/value baseline, broadcasts the environment change, and only then removes activation state. A failed postcheck reports only the variable name, expected/current presence, and whether the value matched; it never prints the value. Failed postchecks or broadcasts retain retryable state. `USER_ENV_MUTATED=YES` is emitted whenever this invocation actually writes or deletes a User-environment value.

## Update and rollback semantics

Every executable/module in the chain, including Node, has an explicit review pin. Replacement-in-place of the adapter, either wrapper, bridge core, Node, or `codex.exe` causes `Validate` and `Enable` to fail closed before any User environment mutation. It is never silently adopted. For an intentional update, the owner closes Codex, reviews the replacement, computes fresh SHA-256 values for all changed files, updates the manifest, runs `Validate`, and explicitly enables again only after `Disable` has completed.

If the bridge bundle is missing or invalid, `CODEX_CLI_PATH` can be disabled with the script's `Disable` operation. The persisted snapshot is local activation metadata only; it contains environment values and paths, never protocol payloads, prompts, commands, tool/file/chat content or tokens.

## Production acceptance boundary

Live ordinary-Desktop acceptance, sink destination choice, and the real child pin are owner-controlled and are not run by the offline test command. #93 completion semantics are intentionally not implemented here.

The automated production-activation test includes a Windows-only isolated registry path under `HKCU\Software\KostGame\K15CodexBridgeTests\<random-id>`. It exercises the same presence/value registry primitive as production, including `PRESENT` empty and a mixed six-variable baseline, then deletes the temporary key in `finally`. It does not use or alter the owner's six bridge variables.
