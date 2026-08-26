# K15 Codex stdio bridge — offline feasibility prototype

This is an **offline research prototype**. It only starts the bundled deterministic fake app-server and must not be pointed at Codex Desktop, a real `codex app-server`, `codex-ipc`, or owner configuration. It has no network code and never writes protocol traffic to disk.

## What is proved in the fixture contract

The bridge sends transport bytes using Node `pipe()` in both directions, preserving the original chunk objects while retaining native backpressure. Child stderr is piped independently and never enters JSONL observation. Observation is a separate `data` listener: an invalid, incomplete, or oversize record remains transport traffic and never becomes a transport failure.

The observer recognizes **fixture assumptions only**, not a claim about a live/private Codex protocol:

```json
{"method":"item/commandExecution/requestApproval","params":{"requestId":"A","threadId":"T","turnId":"U","itemId":"I"}}
{"method":"item/commandExecution/respondApproval","params":{"requestId":"A","decision":"accept"}}
```

The corresponding `item/fileChange/*` methods use the same fixture shape. Requests are correlated by `requestId` plus method family; no timing, focus, process state, toast, or tool-completion heuristic is used. Supported fixture decisions are `accept`, `acceptForSession`, `decline`, and `cancel`. Unknown shapes, decisions, and cross-family responses emit nothing and remain pending rather than inventing a resolution.

Only a fresh allowlisted event is sent to the optional sink: timestamp, source, event name, request ID, decision, and present thread/turn/item IDs. Request payloads are parsed transiently to read these values but are never persisted or forwarded to telemetry. JSONL framing retains at most 64 KiB of an incomplete record and 256 pending IDs. The sink has at most one asynchronous write in flight; errors and overload drop telemetry without blocking the pipes.

## Configuration boundary

`CONFIG_BOUNDARY=NOT_PROVEN`. The repository has no `CODEX_CLI_PATH`, `codex_cli_command`, or app-server selection mechanism. A current official OpenAI documentation search returned no result for `CODEX_CLI_PATH`; therefore this prototype does not establish a supported Desktop bridge-selection boundary. Patching app packaging or attaching private IPC is out of scope and is not proposed.

Run the deterministic, dependency-free suite with:

```text
npm.cmd test
```
