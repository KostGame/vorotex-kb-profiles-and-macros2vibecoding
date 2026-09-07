# Vorotex.K15.Runtime vNext bootstrap

This directory contains the side-by-side vNext runtime increments from issues #140 and #141. It is intentionally small and has no ownership of the installed product or the legacy `status-lab` process.

## Architecture boundary

```text
Vorotex.K15.Runtime process
        owns
        |  normalized RuntimeSnapshot
        v
Vorotex.K15.Runtime.Contracts  <--- future UI clients consume immutable IPC/state contracts

status-lab process  ---------------- independent legacy/research runtime
        (unchanged by this increment)
```

The vNext runtime is the future sole owner of normalized state. UI processes will be clients only; they must not become alternate state owners. The contracts contain no UI framework types, machine-specific paths, HID payloads, credentials, or live capture data.

## Current scope

- `Vorotex.K15.Runtime.Contracts` defines immutable runtime/thread snapshots, plural immutable `activeFlags`, health/version data, schema versioning, and explicit string wire names.
- `RuntimeStateEngine` is a deterministic in-process reducer for native thread status and independent read/unread attention evidence. It uses immutable copy-on-write state, rejects stale observations, ignores legacy hook authority, and exports/imports snapshots without replay heuristics.
- `Vorotex.K15.Runtime` is a console host with deterministic startup/shutdown behavior.
- A BCL named `System.Threading.Mutex` is the single-instance ownership primitive. A dedicated owner thread holds and releases it so shutdown can be requested from any host thread. It is a process guard only; it is not a Windows Service dependency.
- Unknown or invalid enum wire values deserialize to `Unknown` and serialize as `UNKNOWN`. They never silently become `Normal` or another healthy state.
- Startup creates a healthy `NORMAL` snapshot with no threads.
- `Vorotex.K15.Runtime.Tests` is a deterministic executable test harness with no external NuGet dependencies.
- `NativeStatusTransport` is the bounded JSONL seam from the existing bridge into the runtime, and
  `NativeStatusDeliveryQueue` serializes authority records while exposing overflow/degraded health.

The host does not launch or own Codex, read HID, control RGB, mutate the host, install itself, configure autostart, or expose a UI/IPC transport. The bridge is an existing external stdio surface; this increment only accepts its bounded native status records.

## Legacy separation

This increment does not reference or modify `status-lab`. The existing Status Lab remains the owner of its current diagnostic behavior until a separately authorized migration makes the ownership boundary explicit. No installed product files, service registrations, startup entries, or live VOROTEX state are touched.

## Build and test

From the repository root:

```text
dotnet build vnext/Vorotex.K15.Runtime/Vorotex.K15.Runtime.csproj
dotnet run --project vnext/Vorotex.K15.Runtime.Tests/Vorotex.K15.Runtime.Tests.csproj
git diff --check
```

The host prints its initial JSON snapshot and waits for Ctrl+C. The executable tests cover deterministic serialization, stable wire names, fail-closed unknown values, single-instance ownership, cancellation shutdown, all state mappings, aggregate priority, stale/duplicate inputs, the owner-live sticky-WAITING sequence, attention separation, and deterministic rehydration.
