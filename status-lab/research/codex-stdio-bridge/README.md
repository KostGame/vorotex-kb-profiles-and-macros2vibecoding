# K15 Codex stdio bridge — offline feasibility prototype

This is an **offline research prototype**. It only starts the bundled deterministic fake app-server and must not be pointed at Codex Desktop, a real `codex app-server`, `codex-ipc`, or owner configuration. It has no network code and never writes protocol traffic to disk.

## What is proved in the offline contract

The bridge sends transport bytes using Node `pipe()` in both directions, preserving the original chunk objects while retaining native backpressure. Child stderr is piped independently and never enters JSONL observation. Observation is a separate `data` listener: an invalid, incomplete, or oversize record remains transport traffic and never becomes a transport failure.

The Phase C observer recognizes the separately proven live JSON-RPC approval shape:

```json
{"jsonrpc":"2.0","id":1,"method":"item/commandExecution/requestApproval","params":{"threadId":"T","turnId":"U","itemId":"I"}}
{"jsonrpc":"2.0","id":1,"result":{"decision":"accept"}}
```


The corresponding item/fileChange/requestApproval request is also exact allowlisted. Requests are correlated by method family, typed top-level JSON-RPC id, and present threadId/turnId/itemId metadata; number 1 and string "1" cannot alias. Only safe integer numbers and bounded non-empty strings are supported. The live response has no method and must contain an object result.decision with one of accept, acceptForSession, decline, or cancel. The old fixture-only respondApproval/params.requestId model is REMOVED and never resolves a live pending request.

Only a fresh allowlisted event is sent to the optional sink: timestamp, source, event name, typed sanitized RPC correlation (rpcIdType and rpcId), decision, and present thread/turn/item IDs. Request payloads are parsed transiently to read these values but are never persisted or forwarded to telemetry. JSONL framing retains at most 64 KiB of an incomplete record and 256 pending IDs. The sink has at most one asynchronous write in flight; errors and overload drop telemetry without blocking the pipes.

## Configuration boundary

`CONFIG_BOUNDARY=NOT_PROVEN`. The repository has no `CODEX_CLI_PATH`, `codex_cli_command`, or app-server selection mechanism. A current official OpenAI documentation search returned no result for `CODEX_CLI_PATH`; therefore this prototype does not establish a supported Desktop bridge-selection boundary. Patching app packaging or attaching private IPC is out of scope and is not proposed.

Run the deterministic, dependency-free suite with:

```text
npm.cmd test
```


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

The B.1 adapter is a dependency-minimal WinExe apphost published for win-x64. Its .exe is suitable for an owner-controlled CODEX_CLI_PATH experiment: it starts the exact CODEX_BRIDGE_NODE_PATH executable with UseShellExecute=false, passes transparent-wrapper.mjs followed by the original argv through ArgumentList, and forwards stdin/stdout/stderr as raw streams.

The adapter accepts an optional absolute CODEX_BRIDGE_WRAPPER_PATH; otherwise it uses the packaged transparent-wrapper.mjs. The existing CODEX_BRIDGE_CHILD_PATH and optional CODEX_BRIDGE_CHILD_SHA256 remain the wrapper's explicit child boundary. Adapter diagnostics are fixed text only. It does not parse protocol bytes, write payload files, or emit telemetry.

Before any child launch, the adapter resolves its own canonical executable path and rejects CODEX_BRIDGE_NODE_PATH or CODEX_BRIDGE_CHILD_PATH when either resolves back to the adapter, returning configuration exit code 2.

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
implementation:

- item/commandExecution/requestApproval with a top-level id
- item/fileChange/requestApproval with a top-level id

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

Status Lab accepts only the exact sanitized schema from source
`codex_stdio_bridge`. Only `accept` and `acceptForSession` can move a waiting
session to `RUNNING`, and only with an exact available thread/turn
correlation. Existing profile colors, RGB semantics, parallel attention
priority, and `Stop -> DONE_PENDING_ATTENTION` are unchanged.

Phase C offline tests use deterministic fake children and reducer fixtures.
No live Codex Desktop is armed by this repository test command; the owner
controls any later canary and must revalidate current protocol pins first.
