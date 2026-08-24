# Status Lab owner canary — controlled-color effect policy

Date: 2026-08-25

## Physical result

Owner tested the vNext Effect Lab on the real VOROTEX K15.

- Effect Lab behavior is accepted overall.
- Uncontrolled multicolor output during profile switching is rejected.
- Status indication must use only effects whose palette is controlled by Status Lab.
- A notifier effect may use at most one or two explicitly configured colors; no rainbow/self-generated multicolor modes.
- Current profile identity remains RED for A and BLUE for B.
- Default notifier rendering remains one profile color.

## Product policy

Allowed notifier modes:

- Constant
- Flowing Water
- Mono Water
- Single-color breathing
- Off

Rejected for notifier/configurator selection:

- Cycle breathing
- Tetris blocks
- Neon
- Ambilight

The low-level HID protocol mapping may retain those native modes for research/forensics, but canonical Status Lab config validation must reject them.

Profile-switch default changes from Mono Water to a short single-color Flowing Water overlay using the NEW profile color, then resumes the semantic state.

This physical result supersedes the earlier policy where multicolor modes were merely marked as warnings/experimental.