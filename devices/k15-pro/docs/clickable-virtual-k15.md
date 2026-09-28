# Clickable virtual K15 research contract

This document records the public research boundary and proposed architecture for a future clickable Mini-K15 inside Codex Pet. It describes research and design only. No arbitrary virtual K15 dispatcher is enabled by this document.

## Proven research facts

- Active hardware profile slot and local layout definition are separate authorities. The device protocol reads the active slot directly from hardware through command `0x82`, selector `2`.
- A controlled owner-approved transaction `slot 1 -> slot 0 -> read -> slot 1` completed with exact final hardware readback `RESTORE_VERIFY=PASS`.
- Physical or programmatic A/B switching does not rewrite local `Profile0` / `Profile1` / `macroConfig` files. File timestamps or hashes cannot establish which hardware slot is active.
- An official single-profile `.KB.Config` export matched the corresponding live `ProfileN` `KBconfig` for the observed layout. Embedded macro references resolve by exact `grpGuid` plus `macGuid`.
- The OEM feature protocol uses `HidD_SetFeature` as the request envelope for semantic reads and `HidD_GetFeature` for the response. The recovered request builder is OEM function `0x000B6040`; the previous static false-positive security-cookie helper is not a request builder.
- Command `0x84` is the onboard main binding/scan read surface with `selector=0`, `address=cellIndex * 4`, `length=4`. The OEM convenience getter caps its public index argument at `0..15`, while the OEM write path proves a 160-cell (`20 x 8`) main matrix and uses the same `cellIndex * 4` addressing.
- An owner-approved extended read-only canary verified cells `16..47` live with the same `0x84` address contract. Every populated cell matched the corresponding `KBconfig.ini` physical control and every intervening empty matrix cell returned the empty native record `02000000`.
- Command `0x85` is a bounded two-item onboard encoder-action surface: indices `0..1`, address `index * 4`, length `4`. Slot-1 live values contain the expected encoder actions `234/233`.
- Command `0x88` provides live macro payload readback. Its index is a macro-memory/binding slot, not a physical-control index. The first page is `selector=index, address=0, length=32`; additional pages increment address by `32`.
- Full `0x88` payloads from live hardware were decoded and matched current `macroConfig.json` macros byte-for-byte through the active `macSta/macVal/macDly` prefix. Tested live slots uniquely matched `VIBE_10_DONE_RU`, `VIBE_07_CREATE_RU`, `VIBE_09_REVIEW_RU`, and `VIBE_11_STATUS_RU`.
- Macro semantic authority therefore can be checked against hardware payload contents rather than inferred from display names or `MemMacId`.
- The observed owner layout differs materially from historical V1.2 tables. A future virtual Pet must not hardcode semantic actions from README or release tables.

These are bounded observations from the tested device and OEM software build. Together, `KBconfig.ini`, the OEM 160-cell write path, and live `0x84` reads through cell `47` prove the physical binding-cell map for every rendered Mini-K15 control on the tested K15.

## Important index semantics

Do not conflate the protocol index spaces:

- `0x84 index` is part of an onboard scan/binding surface.
- `0x85 index` selects one of two encoder-action records.
- `0x88 index` selects a macro-memory/binding slot.

In particular, a successful `0x88(index=N)` macro match does **not** mean physical control `N`.

## Not yet proven

- Full hardware-versus-local semantic equality has not yet been evaluated for every clickable control in both profile slots.
- The neighboring OEM read families `0x86` and `0x87` are not required for the proven Mini-K15 main binding-cell map; their unrelated device-feature semantics remain outside this implementation slice.
- An onboard whole-layout checksum/revision token is not proven.
- Unknown action families and unsupported native events have no proven safe virtual dispatch mapping.
- A hardware-derived macro match plus the proven physical binding cell is sufficient hardware evidence for that control, but arbitrary virtual dispatch remains blocked until the local semantic model independently resolves the same action and equality is enforced.

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

1. **Attestation research:** COMPLETE for the clickable Mini-K15 physical-control to main binding-cell map on the tested K15.
2. **Hardware layout model:** IMPLEMENTED locally; captures active slot, proven `0x84` binding cells, and required `0x88` macro payloads with a slot-before/slot-after consistency gate.
3. **Local semantic model:** IMPLEMENTED locally; parses profile bindings, resolves exact GUID macros, encodes the active `macSta/macVal/macDly` prefix, and rejects unsupported storage/action values.
4. **Authority synchronization:** IMPLEMENTED locally as an equality evaluator plus readiness session. The session binds readiness to device connection generation, identity, active slot, profile/macro file hashes, and file-mutation generation. Before every future dispatch it re-reads the selected control's live `0x84` binding and, for macros, the live `0x88` payload. Any difference invalidates readiness.
5. **Encoded-action dispatcher:** PLANNER IMPLEMENTED; verified actions are converted to fail-closed `KeyDown` / `KeyUp` / exact-millisecond `Delay` steps, while profile switching remains a separate action. No Windows input backend is connected yet.
6. **Pet interaction:** reuse rendered geometry; verify click/drag separation and no focus steal.
7. **Explicit encoder transaction:** allow only the user gesture above, with fresh read, opposite-slot selection, exact readback, refresh, and verified rollback.

No implementation slice may bypass the attestation gate for arbitrary clickable controls.

## Safety and verification gates

- Do not probe a new HID command live until its exact static read contract is understood and the owner approves that read family.
- Current owner-approved live read families are the already-tested `0x84`, `0x85`, and `0x88`; active-slot `0x82/selector 2` is separately production-proven.
- The physical-control mapping and local semantic equality gate are implemented. Arbitrary virtual actions remain disabled until Pet lifecycle wiring, non-activating hit testing, and the separately tested Windows input backend are complete.
- Test both hardware slots, exact GUID resolution, live macro payload equality, active-prefix behavior, missing references, unsupported action families, reconnect, identity change, and local-file mutation.
- Test that every virtual dispatch uses a fresh hardware slot read, and that encoder selection requires an explicit click plus exact readback and rollback.
- Test all Pet sizes against the rendered hit bounds, click versus drag, and unchanged foreground focus.
- A separate non-installed candidate and harmless input-capture target are required before any owner acceptance canary. Physical-device and real-application dispatch canaries remain separate owner gates.

## Next implementation gate

Wire the readiness session into the Pet and add mouse hit-testing that reuses the rendered control bounds without stealing foreground focus. Keep the Windows input backend disconnected while click-vs-drag behavior, `READY_VERIFIED` gating, and profile-switch routing are tested with fake dispatch. Real `SendInput` acceptance remains a separate owner gate.
