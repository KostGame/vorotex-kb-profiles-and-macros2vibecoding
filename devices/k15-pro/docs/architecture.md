# K15 Pro architecture

## Working model

The tested configuration path has distinct layers:

```text
physical K15 control
        |
        v
VOROTEX internal slot
        |
        v
profile-specific key or macro assignment
        |
        v
official VOROTEX native assignment
        |
        v
K15 onboard state
```

A physical control identity is not the same thing as its currently assigned output. For example, `TOP_2` is stored through `btn_KBKey_KeyPad2`, while one profile may assign Num 2 and another may assign B or a macro.

## Proven apply path

Controlled tests established the following practical path:

1. Generate sanitized profile and macro data offline.
2. Place the generated data where VOROTEX reads its local configuration.
3. Start the official VOROTEX application.
4. VOROTEX correctly parses the generated macro and binding.
5. Perform a native GUI assignment/reassignment for the target physical control.
6. The K15 executes the generated macro.
7. The assignment survives application close and keyboard power cycle.

Automatic device synchronization merely from loading generated files has not been proven.

## Vibecoding control layers

The intended user-facing architecture separates hardware identity, semantic meaning, language, and application behavior:

```text
K15 physical control
        |
        v
semantic trigger
        |
        v
selected locale pack
        |
        v
app-aware dispatcher
        |
        +--> ChatGPT
        +--> Codex
        +--> IDE
        +--> terminal
        +--> browser
```

This makes one physical control context-aware without repeatedly rewriting K15 onboard memory.

### Why language is a separate layer

A separate onboard hardware profile per natural language would scale poorly and consume scarce profile slots. It would also make text macros dependent on the active keyboard layout.

Instead, the physical profile emits stable semantic triggers such as `CHECK`, `FIX`, or `REVIEW`. The host-side dispatcher selects `ru-RU`, `en-US`, `de-DE`, `it-IT`, `zh-CN`, or another locale pack and inserts the corresponding Unicode text.

This separation is particularly important for Chinese, Japanese, and Korean, where IME state makes ordinary keystroke replay an unreliable representation of the intended text.

The canonical language packs are data, not vendor configuration snapshots. They can later feed an AutoHotkey, PowerToys, Stream Deck-style, custom Windows, or other dispatcher without changing their semantics.

## Hardware-family compatibility layer

The project treats K15 Pro as a hardware analogue of the W909 / SXS-W909 family for research and search purposes. Compatibility evidence is tracked separately from the proven K15 configuration model.

Never infer low-level compatibility merely from physical similarity. Firmware, VID/PID, Bluetooth identity, configuration filenames, JSON schema, RGB protocol, and device-write protocol remain K15-specific unless separately demonstrated.

See [`w909-compatibility.md`](w909-compatibility.md).

## Reference design principles

OpenAI's Codex Micro, designed with Work Louder, uses a compact control vocabulary that maps well to K15-class hardware:

- joystick directions for workflow-level actions such as PR review, debugging, and refactoring;
- dedicated command keys for frequent actions such as accept, reject, push-to-talk, and new chat;
- a rotary encoder for changing reasoning level;
- RGB feedback for agent states such as thinking, running, waiting, and done.

Reference: https://openai.com/supply/co-lab/work-louder/

The K15 implementation is independent and adapts these principles to the controls and storage behavior actually verified on VOROTEX hardware.

## Safety boundaries

- Do not publish live vendor configuration snapshots.
- Do not publish firmware or raw device dumps unless their redistribution rights are established.
- Generated profiles should be reproducible from sanitized declarative input.
- Unknown physical-storage mappings remain unsupported rather than inferred.
- Native VOROTEX device writes are preferred until a lower-level protocol is independently understood and justified.
- W909-family similarity must not be promoted to binary/configuration compatibility without direct evidence.
