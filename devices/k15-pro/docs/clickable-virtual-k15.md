# Clickable virtual K15 research contract

This document records the public research boundary and proposed architecture for a future clickable Mini-K15 inside Codex Pet. It describes research and design only. No arbitrary virtual K15 dispatcher is enabled by this document.

## Proven research facts

- Active hardware profile slot and local layout definition are separate authorities. The device protocol reads the active slot directly from hardware through command `0x82`, selector `2`.
- A controlled owner-approved transaction `slot 1 -> slot 0 -> read -> slot 1` completed with exact final hardware readback `RESTORE_VERIFY=PASS`.
- Physical or programmatic A/B switching does not rewrite local `Profile0` / `Profile1` / `macroConfig` files. File timestamps or hashes cannot establish which hardware slot is active.
- An official single-profile `.KB.Config` export matched the corresponding live `ProfileN` `KBconfig` for the observed layout. Embedded macro references resolve by exact `grpGuid` plus `macGuid`.
- The OEM feature protocol uses `HidD_SetFeature` as the request envelope for semantic reads and `HidD_GetFeature` for the response. The recovered request builder is OEM function `0x000B6040`; the previous static false-positive security-cookie helper is not a request builder.
- Command `0x84` is a bounded onboard binding/scan read surface: indices `0..15`, address `index * 4`, length `4`.
- On the tested K15, `0x84[0..14]` matches the first fifteen flattened cells of the vendor `KBconfig.ini` scan matrix, including physical bindings, empty cells, and `LedModeLoop`. `0x84[15]` is a firmware-reserved profile-related record rather than the sixteenth flattened scan cell.
- Command `0x85` is a bounded two-item onboard encoder-action surface: indices `0..1`, address `index * 4`, length `4`. Slot-1 live values contain the expected encoder actions `234/233`.
- Command `0x88` provides live macro payload readback. Its index is a macro-memory/binding slot, not a physical-control index. The first page is `selector=index, address=0, length=32`; additional pages increment address by `32`.
- Full `0x88` payloads from live hardware were decoded and matched current `macroConfig.json` macros byte-for-byte through the active `macSta/macVal/macDly` prefix. Tested live slots uniquely matched `VIBE_10_DONE_RU`, `VIBE_07_CREATE_RU`, `VIBE_09_REVIEW_RU`, and `VIBE_11_STATUS_RU`.
- Macro semantic authority therefore can be checked against hardware payload contents rather than inferred from display names or `MemMacId`.
- The observed owner layout differs materially from historical V1.2 tables. A future virtual Pet must not hardcode semantic actions from README or release tables.

These are bounded observations from the tested device and OEM software build. They do not yet prove the complete physical-control map for every Mini-K15 control.

## Important index semantics

Do not conflate the protocol index spaces:

- `0x84 index` is part of an onboard scan/binding surface.
- `0x85 index` selects one of two encoder-action records.
- `0x88 index` selects a macro-memory/binding slot.

In particular, a successful `0x88(index=N)` macro match does **not** mean physical control `N`.

## Not yet proven

- The complete mapping from every remaining K15 physical control to its onboard binding record is not yet proven.
- The exact role of neighboring OEM read families `0x86` and `0x87` in the remaining layout topology is not yet proven. Static analysis shows `0x87` is a bounded read returning 24 payload bytes that OEM decodes into eight 24-bit records, but no semantic claim is made yet.
- An onboard whole-layout checksum/revision token is not proven.
- Unknown action families and unsupported native events have no proven safe virtual dispatch mapping.
- A hardware-derived macro match proves the macro event stream, but arbitrary virtual dispatch remains blocked until the physical control that owns that binding is also proven.

## Authority and dispatch contract

The mandatory invariant for every virtual control and dispatch time is:

```text
VIRTUAL_ACTION(control,time) == PHYSICAL_K15_ACTION(control,hardware_active_slot_at_time)
```

If the equality cannot be proven, block the virtual click. Never infer actions from release tables, macro display names, cached profile color, or a guessed A/B map.

Recommended authority states:

```text
DISCONNECTED, CONNECTING, SLOT_ONLY, SYNCING_LAYOUT,
READY_VERIFIED, STALE, UNSUPPORTED, ERROR
```

Only `READY_VERIFIED` may dispatch. Before every virtual dispatch, read the active slot from hardware again. Reconnect, device identity change, layout-file mutation, parser failure, unresolved binding, or unsupported action invalidates `READY_VERIFIED` immediately.

Pet must replay encoded actions from the verified hardware-backed snapshot, not semantic labels. Ordinary keyboard macros follow their actual HID usage down/up sequence and active delays. Macro execution uses only the active event prefix. Exact GUID resolution remains the local-file semantic bridge, while live `0x88` payload equality is the hardware proof for macro contents. Unknown action families fail closed.

Pet must reuse the same rendered Mini-K15 bounds for hit testing. Keep click and drag behavior separate. A key click must not drag the Pet, and the Pet must not take foreground focus from the target application.

## Explicit virtual encoder click

An explicit user click on the virtual encoder is the only planned exception to the existing no-programmatic-profile-switch policy.

The proven transaction shape is:

1. Read current hardware slot.
2. Call `SelectActiveSlot(opposite)`.
3. Read the slot again and require the exact expected result.
4. Refresh layout authority and Pet presentation.
5. On any failure, restore the original slot in a `finally` path and verify the restore.

No startup, reconnect, RGB repair, synchronization, or background process may select a profile. Failed or mismatched readback blocks virtual dispatch and invalidates readiness.

## Implementation slices

1. **Attestation research:** complete the physical-control to onboard-binding map for every clickable Mini-K15 control.
2. **Hardware layout model:** read active slot, bounded `0x84/0x85` records, and required `0x88` macro payloads; keep protocol index spaces separate.
3. **Local semantic model:** parse profile and macro data, resolve exact GUID bindings, validate active macro prefixes, and reject unsupported actions.
4. **Authority synchronization:** compare hardware-backed action records with the local semantic model; invalidate readiness on device or file changes and on every parse/validation failure.
5. **Encoded-action dispatcher:** replay only proven native/HID sequences with their active timings. Add explicit support before enabling each action family.
6. **Pet interaction:** reuse rendered geometry; verify click/drag separation and no focus steal.
7. **Explicit encoder transaction:** allow only the user gesture above, with fresh read, opposite-slot selection, exact readback, refresh, and verified rollback.

No implementation slice may bypass the attestation gate for arbitrary clickable controls.

## Safety and verification gates

- Do not probe a new HID command live until its exact static read contract is understood and the owner approves that read family.
- Current owner-approved live read families are the already-tested `0x84`, `0x85`, and `0x88`; active-slot `0x82/selector 2` is separately production-proven.
- Until the complete physical-control mapping is proven, `SLOT_ONLY` or partial attestation may report evidence but must not authorize arbitrary virtual actions.
- Test both hardware slots, exact GUID resolution, live macro payload equality, active-prefix behavior, missing references, unsupported action families, reconnect, identity change, and local-file mutation.
- Test that every virtual dispatch uses a fresh hardware slot read, and that encoder selection requires an explicit click plus exact readback and rollback.
- Test all Pet sizes against the rendered hit bounds, click versus drag, and unchanged foreground focus.
- A separate non-installed candidate and harmless input-capture target are required before any owner acceptance canary. Physical-device and real-application dispatch canaries remain separate owner gates.

## Next research gate

Classify the remaining OEM read families statically, especially the eight-record `0x87` surface. If static evidence shows that a new read family carries the missing physical-control mapping, request a narrow owner-approved read-only canary for that exact command. Do not expand the live allowlist by inference.
