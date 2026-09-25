# K15 Codex stdio bridge — offline feasibility prototype

This is an **offline research prototype**. It only starts the bundled deterministic fake app-server and must not be pointed at Codex Desktop, a real `codex app-server`, `codex-ipc`, or owner configuration. It has no network code and never writes protocol traffic to disk.

## What is proved in the offline contract

The bridge sends transport bytes using Node `pipe()` in both directions, preserving the original chunk objects while retaining native backpressure. Child stderr is piped independently and never enters JSONL observation. Observation is a separate `data` listener: an invalid, incomplete, or oversize record remains transport traffic and never becomes a transport failure.

The Phase C observer recognizes the live JSON-RPC approval families pinned
against OpenAI Codex commit `dfdb40cd0b72dfba3293db5c7c441232e8ef1a60`
(verified 2026-09-26):

- `item/commandExecution/requestApproval`
- `item/fileChange/requestApproval`
- `item/permissions/requestApproval`

The upstream server request declarations are in
[`common.rs`](https://github.com/openai/codex/blob/dfdb40cd0b72dfba3293db5c7c441232e8ef1a60/codex-rs/app-server-protocol/src/protocol/common.rs).
The pinned [`PermissionsRequestApprovalParams`](https://github.com/openai/codex/blob/dfdb40cd0b72dfba3293db5c7c441232e8ef1a60/codex-rs/app-server-protocol/schema/typescript/v2/PermissionsRequestApprovalParams.ts)
contains exact `threadId`, `turnId`, and `itemId`; its response has
`permissions`, `scope` (`turn` or `session`), and optional boolean
`strictAutoReview` ([response type](https://github.com/openai/codex/blob/dfdb40cd0b72dfba3293db5c7c441232e8ef1a60/codex-rs/app-server-protocol/schema/typescript/v2/PermissionsRequestApprovalResponse.ts),
[scope type](https://github.com/openai/codex/blob/dfdb40cd0b72dfba3293db5c7c441232e8ef1a60/codex-rs/app-server-protocol/schema/typescript/v2/PermissionGrantScope.ts)).
This response is not a `result.decision` approval and never resolves
`approval_resolved`.

Command execution and file change requests retain the existing contract:

```json
{"jsonrpc":"2.0","id":1,"method":"item/commandExecution/requestApproval","params":{"threadId":"T","turnId":"U","itemId":"I"}}
{"jsonrpc":"2.0","id":1,"result":{"decision":"accept"}}
```


Those two families are correlated by method family, typed top-level JSON-RPC id, and present threadId/turnId/itemId metadata; number 1 and string "1" cannot alias. Only safe integer numbers and bounded non-empty strings are supported. Their live response has no method and must contain an object result.decision with one of accept, acceptForSession, decline, or cancel. The old fixture-only respondApproval/params.requestId model is REMOVED and never resolves a live pending request.

The permissions family has its own exact correlation path and requires all
three bounded request identifiers. Only an unambiguous same-typed RPC id,
matching request family, and well-formed permission response can emit
`k15-codex-permissions-approval-diagnostic/v1` / `permissions_approval_observed`.
The diagnostic contains source, family, typed RPC id, the request identifiers,
request/response observation timestamps, and only an allowlisted scope and
boolean `strictAutoReview` when present. Permission payloads and all other
request/response fields are discarded. The diagnostic is not consumed by
Status Lab state mapping.

Command/file events sent to the optional sink contain only timestamp, source,
event name, typed sanitized RPC correlation, decision, and present
thread/turn/item IDs. Permissions events use the separate diagnostic schema
below and never contain permission payloads. Request payloads are parsed
transiently to read allowlisted values but are never persisted or forwarded to
telemetry. JSONL framing retains at most 64 KiB of an incomplete record and
256 pending IDs. The sink has at most one asynchronous write in flight; errors
and overload drop telemetry without blocking the pipes.

## Configuration boundary

`CONFIG_BOUNDARY=NOT_PROVEN`. The repository has no `CODEX_CLI_PATH`, `codex_cli_command`, or app-server selection mechanism. A current official OpenAI documentation search returned no result for `CODEX_CLI_PATH`; therefore this prototype does not establish a supported Desktop bridge-selection boundary. Patching app packaging or attaching private IPC is out of scope and is not proposed.

Run the deterministic, dependency-free suite with:

```text
npm.cmd test
```

## Read-only unread probe

`src/unread-probe-cli.mjs` reads only the bounded
`electron-persisted-atom-state/unread-thread-ids-by-host-v1` atom from an
explicit state file. It requires an explicit host discriminator and emits
only sanitized metadata; it does not connect to Codex, open a chat, or write
state:

```text
node src/unread-probe-cli.mjs --state-path <state-file> --host local --thread-id <opaque-thread-id>
```

The probe is diagnostic-only and is not connected to Status Lab state,
lighting, or tray authority. An owner-controlled before/after read proof is
still required before using it as acknowledgement evidence.

The diagnostic `mapUnreadThreadToSession` helper can then join that exact
thread ID only through one safe `state_normalizer/session_state_changed`
record whose DONE correlation contains the same `threadId` and a bounded
`turnId`. Missing or ambiguous joins return `Unknown`; session-ID equality is
never used as a fallback.


## Phase B transparent wrapper

src/transparent-wrapper.mjs is a separate zero-observation entry point for a future owner-controlled canary. It does not import or activate the fixture observer. Its contract is:

- required absolute CODEX_BRIDGE_CHILD_PATH naming the exact reviewed child file;
- optional CODEX_BRIDGE_CHILD_SHA256 pin, verified before launch;
- no PATH scanning and no configured child arguments; Desktop argv is forwarded unchanged;
- native stdin.pipe(child.stdin), child.stdout.pipe(stdout), and child.stderr.pipe(stderr);
- ordinary child exit codes pass through; a child signal maps deterministically to exit code 1;
- missing, recursive, non-file, unsupported, or SHA-mismatched configuration exits 2; spawn failure exits 1.

The wrapper never parses protocol bytes, writes payload files, or emits telemetry. The existing bridge-cli.mjs remains fake-child-only and is not a live launcher. A live Desktop canary is outside this Phase B implementation and must be owner-controlled after architect review.

## Phase B.1 direct Windows executable adapter

The B.1 adapter is a dependency-minimal WinExe apphost published for win-x64. Its .exe is suitable for an owner-controlled CODEX_CLI_PATH experiment. It uses the configured CODEX_BRIDGE_NODE_PATH when present, otherwise the packaged sibling node/node.exe, always with UseShellExecute=false. It passes the selected wrapper followed by the original Desktop argv through ArgumentList and forwards stdin/stdout/stderr as raw streams.

The adapter accepts an optional absolute CODEX_BRIDGE_WRAPPER_PATH; otherwise it uses the packaged transparent-wrapper.mjs. A configured CODEX_BRIDGE_CHILD_PATH outside the standard Desktop runtime root remains an explicit child boundary for isolated tests or owner-controlled custom use. When Desktop invokes the adapter with no child path or with a child under the standard LocalAppData/OpenAI/Codex/bin runtime root, the adapter derives compatibility from the currently running OpenAI.Codex WindowsApps package.

For the standard Desktop path, the adapter mirrors the stock Windows core relocation contract. It reads exactly four package executables in this order: codex.exe, codex-code-mode-host.exe, codex-windows-sandbox-setup.exe, and codex-command-runner.exe. The generation ID is the first 16 lowercase hex characters of SHA-256 over each exact filename, a NUL byte, that file's lowercase SHA-256 digest, and another NUL byte. If the corresponding LocalAppData generation already contains exactly those four byte-identical regular files, it is reused unchanged. Otherwise an expected-name-only incomplete or mismatched generation is rebuilt through a same-root staging directory, every copied file is re-hashed, and the staging directory is atomically renamed into the generation. A generation containing any foreign entry fails closed instead of being deleted or adopted. Old valid generations are not cleaned by the bridge.

This compatibility selection never rewrites CODEX_BRIDGE_CHILD_PATH, CODEX_BRIDGE_CHILD_SHA256, the production manifest, User/Machine environment, or WindowsApps. The only compatibility write is stock-compatible materialization under the standard LocalAppData/OpenAI/Codex/bin runtime root when the current package generation is absent or incomplete. The selected current child path and SHA are supplied only to the wrapper subprocess. If Desktop is not active, an existing explicit child remains the fallback boundary. Adapter diagnostics are fixed text only. It does not parse protocol bytes, write protocol payload files, or emit telemetry.

Before any child launch, the adapter resolves its own canonical executable path and rejects CODEX_BRIDGE_NODE_PATH or CODEX_BRIDGE_CHILD_PATH when either resolves back to the adapter, returning configuration exit code 2. Missing current-package core files, foreign residue in the target generation, copy/hash verification failure, or an unavailable wrapper/Node fail closed.

Build the offline package and fake child with npm.cmd test.

The command publishes both local win-x64 apphosts and runs the offline executable-boundary tests. It does not start Codex Desktop or a live Codex app-server.

## Phase C approval observer

`src/approval-wrapper.mjs` is a separate opt-in entry point layered on the
transparent wrapper. It adds only bounded `data` listeners; forwarding still
uses the Phase B native pipes and the Phase B entry point remains
zero-observation. The optional `CODEX_BRIDGE_APPROVAL_SINK_PATH` must be an
absolute path. When set, the observer appends only the versioned sanitized
`k15-codex-approval/v1` event shape to that path; when absent, no side-channel
file is created.

The live-allowlisted request families are the only protocol assumptions in this
implementation (see the immutable upstream pin above):

- item/commandExecution/requestApproval with a top-level id
- item/fileChange/requestApproval with a top-level id
- item/permissions/requestApproval with a top-level id

Correlation is keyed by exact typed top-level RPC id plus family; `accept`,
`acceptForSession`, `decline`, and `cancel` stay distinct. The legacy fixture-only
respondApproval/params.requestId shape is REMOVED and never resolves a live
pending request. The sanitized event uses rpcIdType and rpcId instead of
pretending the live top-level id was a requestId. Unknown,
malformed, unmatched, duplicate, stale, cross-family, and oversize records
produce no semantic event. No generic `serverRequest/resolved`, timers,
focus/toast state, process polling, completion timing, or Desktop heuristics
are used. The observer never persists or forwards raw protocol bytes or
content and a sink error/overload is fail-open for transport.

Permissions responses follow the separate diagnostic schema above; they are
never converted into `approval_resolved` and do not alter reducer semantics.

Status Lab accepts only the exact sanitized schema from source
`codex_stdio_bridge`. Only `accept` and `acceptForSession` can move a waiting
session to `RUNNING`, and only with an exact available thread/turn
correlation. Existing profile colors, RGB semantics, parallel attention
priority, and `Stop -> DONE_PENDING_ATTENTION` are unchanged.

Phase C offline tests use deterministic fake children and reducer fixtures.
No live Codex Desktop is armed by this repository test command; the owner
controls any later canary and must revalidate current protocol pins first.

## vNext native status observer

`NativeThreadStatusObserver` listens to the same server chunks as the approval
observer and recognizes only the generated `ThreadStatus` union: `active`
requires `activeFlags`, while `idle`, `notLoaded`, and `systemError` omit it
and sanitize to `activeFlags: []`. It emits the bounded
`k15-codex-thread-status/v1` record (`threadId`, status, known flags, bounded
timestamp, and optional `user`/`service`/`canary` classification). A serialized
queue of 64 records preserves arrival order; overflow and sink failures are
reported as fixed health envelopes and never block the transparent `pipe()`
transport. The vNext runtime accepts these records through its dedicated
current-user-only named-pipe ingress (`\\.\pipe\Vorotex.K15.Runtime.NativeAuthority.v1`)
and owns all state mapping. The bridge connects to the already-running Runtime
and never spawns a Runtime process; the general Runtime UI command pipe remains
separate.
# Codex stdio bridge

## Proven Runtime metadata path

The bridge reads `params.thread.id` and `params.thread.cwd` only from the
Codex App Server `thread/started` notification. The App Server protocol defines
`Thread.cwd` as the captured working directory and `thread/started` carries the
thread object. The bridge emits these two bounded UTF-8 values as the separate
versioned `k15-codex-thread-metadata/v1` event. It never derives cwd from
status, prompts, tool items, model data, or arbitrary RPC content.

`thread/status/changed` remains the native status authority path; metadata is
merged by exact thread id in Runtime and cannot affect status ordering or
authority health. Stdio remains byte-transparent; authority delivery is an
optional fail-open connection to the already-running Runtime pipe.
