# Production activation kit

This kit is an owner-run, reversible activation boundary for ordinary Codex Desktop. It does not patch WindowsApps, inject into a process, inspect handles/memory, or modify Machine environment. The checked-in files are activation tooling and a manifest template; this task does not arm the owner's machine.

## Architecture

The owner keeps a reviewed bundle at a stable per-user path:

`ordinary Codex Desktop -> User CODEX_CLI_PATH -> K15.CodexBridge.WindowsAdapter.exe -> approval-wrapper.mjs -> exact codex.exe`

`Activate-CodexBridge.ps1` requires production manifest v2. It validates the exact non-reparse adapter, Node, approval wrapper, transparent wrapper, bridge core, `runtime-process-authority.mjs`, `codex.exe`, and sibling `codex-code-mode-host.exe` paths plus a SHA-256 pin for every file before any environment change. The two Codex runtime files must be in the same generation directory; the script never searches for or adopts another generation.

The approval wrapper connects to the already-running `Vorotex.K15.Runtime.exe` through the fixed `\\.\pipe\Vorotex.K15.Runtime.NativeAuthority.v1` ingress. There is no runtime-command environment variable and the bridge never spawns a Runtime process. The Runtime owns the single-instance lease and the dedicated ingress; the general UI command pipe remains separate.

`Enable` snapshots all bridge-owned User variables atomically to activation-state v3 and records the exact SHA-256 of the approved manifest bytes together with a bounded, sorted inventory of direct child generation directories under the approved runtime bin root. Inventory metadata contains only the generation name and whether `codex.exe` and `codex-code-mode-host.exe` are present; it does not contain prompts, protocol data, user content, or candidate hashes. Machine environment and package files remain untouched.

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

After active writes, `Enable` independently rereads all six variables and requires the exact active presence/value state before reporting `ACTIVE=YES`. `Disable` restores and independently rereads the recorded presence/value baseline, broadcasts the environment change, and only then removes activation state. It can restore both legacy activation-state v2 and current v3. A failed postcheck reports only the variable name, expected/current presence, and whether the value matched; it never prints the value. Failed postchecks or broadcasts retain retryable state. `USER_ENV_MUTATED=YES` is emitted whenever this invocation actually writes or deletes a User-environment value.

## Update and rollback semantics

Every executable/module in the chain, including Node and `runtime-process-authority.mjs`, has an explicit review pin. Replacement-in-place of the adapter, either wrapper, bridge core, authority module, Node, `codex.exe`, or `codex-code-mode-host.exe` causes `Validate` and `Enable` to fail closed before any User environment mutation. It is never silently adopted.

`Status` is read-only and reports `ACTIVE=YES|NO` plus one bounded runtime health state: `INACTIVE`, `HEALTHY`, `CHILD_RUNTIME_STALE`, or `UPDATE_REVALIDATION_REQUIRED`. `INACTIVE` means no activation state exists. `HEALTHY` requires a valid v3 state, an unchanged canonical manifest path and exact manifest-byte SHA-256, a valid v2 manifest, exact active User environment, an intact pinned child/host pair, and an unchanged runtime inventory. `CHILD_RUNTIME_STALE` means the approved pair or activation is no longer valid. `UPDATE_REVALIDATION_REQUIRED` means the approved pair is intact but the manifest identity/path or runtime inventory changed; a new coherent or partial generation is not an approval.

Canonical future update flow:

```text
Status
→ UPDATE_REVALIDATION_REQUIRED

owner reviews the new coherent generation
→ closes Codex
→ Disable existing activation
→ prepares a reviewed manifest v2 with exact child + host pins
→ Validate
→ Enable
→ launch Codex once
→ Status HEALTHY
→ native Code Mode canary
```

If the new runtime is not accepted, run `Disable`; the stock baseline remains available. The script never repins, copies binaries, rewrites the manifest, changes User environment, or disables/enables automatically.

Legacy v1 manifests are rejected by `Validate` and `Enable`. A legacy v2 activation state remains disable-compatible and reports `UPDATE_REVALIDATION_REQUIRED` with `REASON=LEGACY_STATE_NO_RUNTIME_BASELINE` until the owner performs the reviewed v2 Disable → Validate → Enable lifecycle.

If the bridge bundle is missing or invalid, `CODEX_CLI_PATH` can be disabled with the script's `Disable` operation. The persisted snapshot is local activation metadata only; it contains environment values and paths, never protocol payloads, prompts, commands, tool/file/chat content or tokens.

## Production acceptance boundary

Live ordinary-Desktop acceptance, sink destination choice, and the real child pin are owner-controlled and are not run by the offline test command. #93 completion semantics are intentionally not implemented here.

The automated production-activation test includes a Windows-only isolated registry path under `HKCU\Software\KostGame\K15CodexBridgeTests\<random-id>`. It exercises the same presence/value registry primitive as production, including `PRESENT` empty and a mixed six-variable baseline, then deletes the temporary key in `finally`. It does not use or alter the owner's six bridge variables.
