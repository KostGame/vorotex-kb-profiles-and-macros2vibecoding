# Status Lab owner canary — 2026-08-24

This note records sanitized findings from the first Windows owner canary. Raw local notification/history data is intentionally not committed.

## Result

```text
WINDOWS_NOTIFICATION_ACCESS = PASS
WINDOWS_NOTIFICATION_POLL = PASS
OPENAI_NOTIFICATION_IDENTITY = OBSERVED
CODEX_HOOK_EVENTS = NOT_YET_OBSERVED
INITIAL_HOOK_TARGET = WRONG_CODEX_HOME
```

## Windows notification channel

The Status Lab process received `Allowed` from `UserNotificationListener` and successfully observed current, added, and removed toast records.

The OpenAI desktop package surfaced as:

```text
appName = ChatGPT
AppUserModelId = OpenAI.Codex_2p2nqsd0c76g0!App
PackageFamilyName = OpenAI.Codex_2p2nqsd0c76g0
```

Important implication: Windows app identity alone does not distinguish a ChatGPT toast from a Codex toast inside this package. Future attribution should correlate notification timing with Codex lifecycle hooks rather than assume every OpenAI-package toast belongs to Codex.

The canary also started with a backlog of existing OpenAI notifications. Adding a new notification could coincide with eviction/removal of an old notification. Therefore the future notifier must:

1. snapshot existing notification IDs as baseline at startup;
2. treat only notifications added after baseline as new attention candidates;
3. never interpret removal of an old baseline notification as user acknowledgement;
4. clear a pending state only when the specific post-baseline notification being tracked disappears or another higher-confidence source clears it.

## Codex hooks channel

The first installer revision reported success but wrote only:

```text
%USERPROFILE%\.codex\hooks.json
```

The active AgentLoop-oriented Codex session was visibly loading its skills from:

```text
%USERPROFILE%\.codex-agentloop\...
```

No `codex_hook` records appeared in the first canary journal. This is consistent with installing hooks into the wrong Codex home, not with a failure of the hook schema itself.

The installer was corrected to discover existing Codex homes, including:

```text
%USERPROFILE%\.codex-agentloop
%USERPROFILE%\.codex
%USERPROFILE%\.codex-*
```

and to verify the serialized handlers after each write.

The next canary must fully restart Codex after installation and then verify:

```text
UserPromptSubmit
PermissionRequest
Stop
SessionEnd
```

## Privacy

Raw notification title/body and Codex prompt/assistant/tool-input content remain excluded from the journal.

The next build records only a SHA-256 fingerprint, text-element lengths, and coarse in-memory keyword hints for notification classification. This allows correlation without storing notification text.
