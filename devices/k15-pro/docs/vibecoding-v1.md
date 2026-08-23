# VIBECODING v1 draft

Status: **design candidate**, not a released profile.

The goal is a one-hand control surface for ChatGPT, Codex, AgentLoop-style workflows, and general development work while keeping destructive actions deliberate.

## UX principles

The layout follows four principles inspired by the OpenAI + Work Louder Codex Micro control model:

1. frequent commands live on dedicated keys;
2. workflow-level actions are good joystick candidates;
3. rotary input is a natural fit for a continuously adjustable setting such as reasoning level;
4. lighting can later expose agent state.

Reference: https://openai.com/supply/co-lab/work-louder/

## Hardware profile 1: AI / VIBECODING

Candidate standard-key semantics:

| Key | Semantic action | Initial intent |
|---|---|---|
| `1` | `CHECK` | check current work |
| `2` | `NEXT` | proceed to the next step |
| `3` | `AGENT_PROMPT` | prepare the next agent prompt |
| `4` | `FIX` | fix identified issues |
| `5` | `PUBLISH` | prepare/publish current result |
| `6` | `MERGE` | prepare/merge approved change |
| `7` | `CREATE` | create requested artifact/change |
| `8` | `CONTINUE` | continue current work |
| `9` | `REVIEW` | perform review |
| `0` | `TEST` | run/inspect tests |
| `.` | `STATUS` | report current status |
| `Enter` | `NEW_LINE` | insert a newline without submitting |
| `-` | `REJECT_OR_STOP` | reject/stop current path |
| `+` | `ACCEPT_OR_APPROVE` | accept/approve current path |
| `Space` | `PUSH_TO_TALK` | voice input trigger where supported |

Destructive or consequential commands should insert/prepare an action but should not submit it automatically. Submission remains a separate physical action.

## Newline vs submit

A core requirement is to separate multiline composition from submission:

```text
K15 Enter -> NEW_LINE
joystick click -> SUBMIT
```

The dispatcher can translate `NEW_LINE` per application, for example `Shift+Enter`, `Ctrl+Enter`, or plain `Enter` depending on the focused application.

Joystick-click storage on K15 has not yet been proven, so `SUBMIT` remains a desired mapping pending hardware verification.

## Joystick candidate workflows

If joystick programming is confirmed, preferred semantics are workflow-level rather than cursor-level:

| Direction | Candidate semantic |
|---|---|
| Up | `PR_REVIEW` |
| Down | `DEBUG_OR_FIX` |
| Left | `REFACTOR` |
| Right | `VERIFY_OR_TEST` |
| Click | `SUBMIT` |

Fallback while storage is unresolved: retain native cursor behavior.

## Encoder candidate behavior

Preferred AI-profile behavior, if remapping is proven:

- rotate left: reasoning level down;
- rotate right: reasoning level up;
- click: switch hardware profile.

Fallback: retain native volume control and profile switching.

## Hardware profile 2: SYSTEM

The second onboard profile is reserved for desktop/navigation shortcuts. Exact assignments will be selected after the AI profile is tested in real use.

## App-aware dispatcher

Long Russian commands and application-specific shortcuts should preferably live in a Windows-side dispatcher rather than as long onboard text macros.

Example:

```text
CHECK
  ChatGPT -> insert "Проверь"
  Codex   -> insert a Codex-specific review/check instruction
  IDE     -> run the configured verification action
```

This keeps the K15 profile stable while allowing application-specific behavior to evolve independently.

## Future status lighting

If K15 lighting proves externally controllable without unsafe device operations, a later version can map agent state to visual feedback: idle, thinking, running, waiting, done, and blocked.
