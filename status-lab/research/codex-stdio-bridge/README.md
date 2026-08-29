# K15 Codex stdio bridge — offline feasibility prototype

This is an **offline research prototype**. It only starts the bundled deterministic fake app-server and must not be pointed at Codex Desktop, a real `codex app-server`, `codex-ipc`, or owner configuration. It has no network code and never writes protocol traffic to disk.

## What is proved in the fixture contrac

The bridge sends transport bytes using Node `pipe()` in both directions, preserving the original chunk objects while retaining native backpressure. Child stderr is piped independently and never enters JSONL observation. Observation is a separate `data` listener: an invalid, incomplete, or oversize record remains transport traffic and never becomes a transport failure.

The observer recognizes **fixture assumptions only**, not a claim about a live/private Codex protocol:

```json
{"method":"item/commandExecution/requestApproval","params":{"requestId":"A","threadId":"T","turnId":"U","itemId":"I"}}
{"method":"item/commandExecution/respondApproval","params":{"requestId":"A","decision":"accept"}}


The corresponding `item/fileChange/*` methods use the same fixture shape. Requests are correlated by `requestId` plus method family; no timing, focus, process state, toast, or tool-completion heuristic is used. Supported fixture decisions are `accept`, `acceptForSession`, `decline`, and `cancel`. Unknown shapes, decisions, and cross-family responses emit nothing and remain pending rather than inventing a resolution.

Only a fresh allowlisted event is sent to the optional sink: timestamp, source, event name, request ID, decision, and present thread/turn/item IDs. Request payloads are parsed transiently to read these values but are never persisted or forwarded to telemetry. JSONL framing retains at most 64 KiB of an incomplete record and 256 pending IDs. The sink has at most one asynchronous write in flight; errors and overload drop telemetry without blocking the pipes.

## Configuration boundary

`CONFIG_BOUNDARY=NOT_PROVEN`. The repository has no `CODEX_CLI_PATH`, `codex_cli_command`, or app-server selection mechanism. A current official OpenAI documentation search returned no result for `CODEX_CLI_PATH`; therefore this prototype does not establish a supported Desktop bridge-selection boundary. Patching app packaging or attaching private IPC is out of scope and is not proposed.

Run the deterministic, dependency-free suite with:

```tex
npm.cmd tes


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
