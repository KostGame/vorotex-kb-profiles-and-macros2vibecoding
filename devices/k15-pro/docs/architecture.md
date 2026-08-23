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

The intended user-facing architecture separates hardware from application semantics:

```text
K15 physical control
        |
        v
semantic trigger
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
