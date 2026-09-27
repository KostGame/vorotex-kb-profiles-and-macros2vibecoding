# Clickable virtual K15 research contract

This document records the public research boundary and proposed architecture for a future clickable Mini-K15 inside Codex Pet. It describes research and design only. No virtual K15 dispatcher or profile-switch feature is implemented by this document.

## Proven research facts

- Active hardware profile slot and local layout definition are separate authorities. The already-proven device read protocol reads the active slot directly from hardware.
- Independent readback observed slot 0 before one owner physical encoder switch and slot 1 after that switch.
- Physical A/B switching did not modify local `Profile0` / `Profile1` / `macroConfig` files. Their timestamps or hashes cannot establish which hardware slot is active.
- An official single-profile `.KB.Config` export matched the corresponding live `ProfileN` `KBconfig` for all relevant K15 controls in the observed layout. Embedded macro references resolved by exact `grpGuid` plus `macGuid`.
- Macro action authority is the bound GUID pair and the active `macData` prefix `[0:num]`. Display names and `MemMacId` semantics are not dispatch authority.
- The observed owner layout differed materially from the historical V1.2 release table. A future virtual Pet must not hardcode semantic actions from README or release tables.

These are bounded observations from the tested device and software state. They do not establish a general onboard layout readback capability.

## Not yet proven

- Full onboard key-binding and macro readback is not proven.
- An onboard configuration checksum, revision, or equivalent hardware-derived attestation token is not proven.
- Local VOROTEX files cannot attest a keyboard that may have been reconfigured elsewhere.
- Unknown action families and unsupported native events have no proven safe virtual dispatch mapping.

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

Pet must replay encoded actions from the verified layout snapshot, not semantic labels. Ordinary keyboard macros follow their actual HID usage down/up sequence and active delays. Macro execution uses only `[0:num]`. Unknown action families fail closed. Resolve macro references through exact bound GUIDs; do not use names or `MemMacId` as semantic identity.

Pet must reuse the same rendered Mini-K15 bounds for hit testing. Keep click and drag behavior separate. A key click must not drag the Pet, and the Pet must not take foreground focus from the target application.

## Explicit virtual encoder click

An explicit user click on the virtual encoder is the only planned exception to the existing no-programmatic-profile-switch policy. This is future design only; current Status Lab does not programmatically switch profiles.

The planned transaction is:

1. Read current hardware slot.
2. Call `SelectActiveSlot(opposite)`.
3. Read the slot again and require the exact expected result.
4. Refresh layout authority and Pet presentation.

No startup, reconnect, RGB repair, synchronization, or background process may select a profile. Failed or mismatched readback blocks virtual dispatch and invalidates readiness.

## Implementation slices

1. **Attestation research:** prove the smallest onboard primitive that binds the relevant onboard layout to hardware: full relevant readback, a deterministic configuration checksum/revision, or an equivalent hardware-derived token.
2. **Layout model:** parse local profile and macro data, resolve exact GUID bindings, validate active macro prefixes, and reject unsupported actions.
3. **Authority synchronization:** keep slot observation separate from layout attestation; invalidate readiness on device or file changes and on every parse/validation failure.
4. **Encoded-action dispatcher:** replay only proven native/HID sequences with their active timings. Add explicit support before enabling each action family.
5. **Pet interaction:** reuse rendered geometry; verify click/drag separation and no focus steal.
6. **Explicit encoder transaction:** allow only the user gesture above, with fresh read, opposite-slot selection, exact readback, and refresh.

No implementation slice may bypass the attestation gate for arbitrary clickable controls.

## Safety and verification gates

- First gate is read-only protocol research. Do not probe unknown write commands. Continue research only after the exact read contract is proven.
- Until onboard attestation is proven, `SLOT_ONLY` may report hardware slot but must not authorize arbitrary virtual actions.
- Test both slots, exact GUID resolution, active-prefix behavior, missing references, unsupported action families, reconnect, identity change, and local-file mutation.
- Test that every virtual dispatch uses a fresh hardware slot read, and that encoder selection requires an explicit click plus exact readback.
- Test all Pet sizes against the rendered hit bounds, click versus drag, and unchanged foreground focus.
- A separate non-installed candidate and harmless input-capture target are required before any owner acceptance canary. Physical-device and real-application canaries remain separate owner gates.

## Next research gate

Before arbitrary clickable dispatch, prove the smallest onboard layout attestation primitive: full relevant readback, deterministic onboard configuration checksum/revision, or equivalent hardware-derived token. Keep research read-only until its exact protocol contract is proven.
