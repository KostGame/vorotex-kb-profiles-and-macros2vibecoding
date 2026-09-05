# Codex read acknowledgment — Issue #123

Status Tray observes the persisted `electron-persisted-atom-state/unread-thread-ids-by-host-v1`
atom. StateReducer remains the only authority that clears DONE. Window focus,
navigation, toasts and Dashboard activity are not read receipts.

## Causal contract

An eligible completion has an exact, unique session/thread/turn binding, a completion
generation and a runtime epoch. The existing sanitized `turn_completed` bridge route
supplies thread/turn correlation. The unread reader never joins an unread ID directly
to a hook session ID.

After completion, a fresh HasUnread observation arms that binding. Two later valid
NoUnread snapshots produce ready evidence. The reducer checks that the same completion
is still DONE before emitting `codex_read_ack`. Ready evidence is retained until it is
applied or the exact completion stops being eligible. Unrelated reorder-pending events
do not discard or block its delivery; a pending event affecting the same completion
does block delivery until the event is reduced.

If Stop arrives before the bridge completion, the later exact unique bridge event can
enrich that same DONE generation without another state transition or aggregate change.
Conflicting correlation disables read ACK for that generation. Replaying the raw
sanitized bridge event restores the binding, including when the earlier Stop diagnostic
had no thread ID.

| Situation | Result |
| --- | --- |
| Initial NoUnread, including after restart | Preserve DONE |
| Rehydrated exact completion, fresh HasUnread then two NoUnread in the new epoch | ACK that completion |
| Missing/malformed source, missing host, duplicate host/ID, ambiguous correlation | Fail closed |
| Unknown/Unavailable between unread and read samples | Require fresh HasUnread |
| Already proven ready evidence, unrelated journal activity | Retry delivery |
| Same-session new turn/generation | Invalidate old evidence |
| Duplicate ACK | No second transition |
| Late Stop after ACK for the exact same completion | No second DONE |
| Stop for a new turn | Existing Stop behavior |
| A read while B is DONE | Clear A; retain B and aggregate DONE |

Startup never creates an ACK. The existing journal replay reconstructs attention from
hook/bridge inputs; it does not replay ACK diagnostics as commands. Consequently an
already read completion can reappear as DONE after restart and remain so if its initial
state is NoUnread. This is the deliberate fail-closed restart boundary.

## Bounds and privacy

Status Tray requires an explicit absolute `CODEX_HOME` in its process environment and
observes only the `local` host. It does not fall back to a potentially stale other
profile. Without that source or without a proven exact correlation, DONE remains.

The observer reads at most once per second, once for all eligible completions, and
does no source I/O when none are eligible. Limits are 16 MiB per file, JSON depth 64,
256 hosts, 10,000 unread IDs across hosts, and 256 tracked completion bindings. Overflow
stops new observations while already proven eligible evidence remains deliverable.
Each read reopens the file with read/write/delete sharing, checks size and write-time
stability, and validates the complete JSON. The store is never written.

Semantic identities are ordinal and limited to 128 UTF-8 bytes; they are rejected,
never truncated into another identity. Only bounded IDs, times, host, generation,
epoch and reason enter `read_ack_evidence`. Detailed Logging OFF retains that allowlisted
record and the ordinary per-session/aggregate transitions. Dashboard only adds the
reason allowlist entry; Issue #124 UI/SSE behavior is outside this change.

## Validation and deployment boundary

`StateReducerSmoke.csproj` includes the read-ACK reader, reducer, observer, replay,
privacy, bounds and actual normalizer-loop scenarios. `LiveDashboardBehaviorSmoke.csproj`
checks display/sanitization of the new reason. These run through the existing Status Lab
CI workflow. Tests use synthetic IDs and isolated journals.

The live authority proof used the previously accepted Windows adapter and bridge with
an explicitly approved, hash-pinned temporary manifest. The route is Codex Desktop →
Windows adapter → byte-transparent bridge → real Codex backend; only allowlisted
`k15-codex-completion/v1` metadata reaches the Status Lab journal. Raw protocol and
conversation content are not captured.

Temporary activation is not production deployment. After its rollback, the original
owner configuration must be restored. If that configuration does not emit bridge
completion events, this implementation will retain DONE instead of guessing a binding.
Permanent operation requires a separately approved post-merge installation/activation
of the reviewed bridge route (see `research/codex-stdio-bridge/production/README.md`),
current executable/hash validation, an explicit Status Tray Codex home and a new live
exact-correlation canary. This PR grants none of that deployment authority.

Live results and rollback verification are recorded separately in the implementation
report. Agent validation does not constitute independent architect verification.

### Live observations (2026-09-05, UTC)

The temporary candidate was built from the Astra implementation after the Stop-first
enrichment and retained-delivery fixes. Its Status Tray SHA-256 was
`7a8c79e73eb6e8ba05b2505b5531f05d90afbcc602ad3dfa0adeb72356f7242a`.

| Check | Observed result |
| --- | --- |
| A and B complete unread, 10:23:16 | Both DONE + HasUnread; another session RUNNING |
| Read only A, ACK at 10:23:43 | A NORMAL, B DONE + HasUnread; DONE count 2 → 1 |
| Read B, ACK at 10:24:56 | A/B NORMAL; DONE count 1 → 0; aggregate RUNNING |
| Dashboard observer | Both per-session NORMAL rows retain `codex_read_ack` |
| Restart candidate, 10:26:57 | A/B initial NoUnread remain rehydrated DONE; C HasUnread remains DONE |
| Read C in new epoch, ACK at 10:29:32 | C NORMAL with exact persisted completion; A/B initial-NoUnread DONE retained |
| Unrelated attention | Real permission-request WAITING activity occurred during the test; it did not clear C. Deterministic tests also cover A ACK while B remains WAITING. |
| Last unread | Not proven: even when the owner saw only C, persisted local count was 3; after reading C it remained 2. No empty-host interpretation was added. |
| Temporary runtime rollback | Original installed Tray restored; zero candidate processes; original Dashboard absence preserved; configuration hashes unchanged |

The first epoch's two ACKs used the same runtime epoch; C's ACK used a different epoch,
with HasUnread and both NoUnread samples after restart. Only sanitized completion/read
metadata and counts were retained. The owner-reported unread badge did not establish a
host/thread mapping and was not used as authority.

### Post-restore verification (2026-09-05)

Owner EXACT_RESTORE_002 completed six writes and reported PASS. Independent read-only
Diagnose after ordinary Desktop startup returned PASS with zero writes: all six recorded
User variables are PRESENT and match the baseline exactly; none matches temporary active
configuration. The real hash-pinned backend app-server has Desktop as its direct parent;
no temporary adapter or bridge process is running. The installed Tray executable and
three saved configuration hashes match the pre-canary snapshot. Its process path was
verified with QueryFullProcessImageName because WMI returned an unavailable path. The
installed Dashboard was subsequently started by that restored Tray.

All six bridge-related Machine variables are absent. Both rollback implementations write
only User scope and owner results report no Machine or package mutation. No pre-activation
snapshot of the complete Machine environment exists; this is operation-scoped evidence,
not a claim of a whole-Machine before/after comparison. The temporary rollback gate is
closed; permanent production completion-source activation remains a separate gate.

The old Disable failed after mutation: Windows PowerShell 5/.NET Framework treated each
recorded PRESENT empty string passed to SetEnvironmentVariable as deletion. Canonical
Disable then removed activation-state, and the wrapper's first restore comparison failed.
Its USER_ENV_MUTATED=NO field was incorrect bookkeeping. Recovery used the hash-pinned
pre-activation backup, preserved PRESENT empty values through exact registry writes, and
verified presence and value equality. The old Disable must not be reused for this baseline.
