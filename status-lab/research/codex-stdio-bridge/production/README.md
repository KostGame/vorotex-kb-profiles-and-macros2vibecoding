# Production activation kit

This kit is an owner-run, reversible activation boundary for ordinary Codex Desktop. It does not patch WindowsApps, inject into a process, inspect handles/memory, or modify Machine environment. The checked-in files are activation tooling and a manifest template; this task does not arm the owner's machine.

## Architecture

The owner keeps a reviewed bundle at a stable per-user path:

`ordinary Codex Desktop -> User CODEX_CLI_PATH -> K15.CodexBridge.WindowsAdapter.exe -> approval-wrapper.mjs -> exact codex.exe`

`Activate-CodexBridge.ps1` validates the exact non-reparse adapter, Node, approval wrapper, transparent wrapper, bridge core and child paths plus a SHA-256 pin for every file before any environment change. It snapshots all bridge-owned User variables atomically to `activation-state.json`, then sets only User-level variables. Machine environment and package files remain untouched.

The optional approval sink is configured explicitly in the manifest. Empty means no side-channel file is created. The adapter and wrappers continue to forward transport independently of observer/sink failure.

## Owner operations

Run `Validate` after reviewing/copying a bundle and before `Enable`:

```powershell
pwsh -NoProfile -File .\Activate-CodexBridge.ps1 -Mode Validate -ManifestPath .\manifest.json
pwsh -NoProfile -File .\Activate-CodexBridge.ps1 -Mode Enable -ManifestPath .\manifest.json
pwsh -NoProfile -File .\Activate-CodexBridge.ps1 -Mode Status -ManifestPath .\manifest.json
pwsh -NoProfile -File .\Activate-CodexBridge.ps1 -Mode Disable -ManifestPath .\manifest.json
```

Restart Codex Desktop after Enable/Disable so a newly launched process receives the changed User environment. Disable restores the exact pre-activation values, including absence versus empty value, in one bounded operation. If activation or validation fails, the stock startup remains available; no fallback child is selected.

## Update and rollback semantics

Every executable/module in the chain, including Node, has an explicit review pin. Replacement-in-place of the adapter, either wrapper, bridge core, Node, or `codex.exe` causes `Validate` and `Enable` to fail closed before any User environment mutation. It is never silently adopted. For an intentional update, the owner stops Codex, reviews the replacement, computes fresh SHA-256 values for all changed files, updates the manifest, runs `Validate`, and explicitly enables again after `Disable`; `-Force` is reserved for an owner who has already preserved the state and intentionally revalidated the same manifest.

If the bridge bundle is missing or invalid, `CODEX_CLI_PATH` can be disabled with the script's `Disable` operation. The persisted snapshot is local activation metadata only; it contains environment values and paths, never protocol payloads, prompts, commands, tool/file/chat content or tokens.

## Production acceptance boundary

Live ordinary-Desktop acceptance, sink destination choice, and the real child pin are owner-controlled and are not run by the offline test command. #93 completion semantics are intentionally not implemented here.
