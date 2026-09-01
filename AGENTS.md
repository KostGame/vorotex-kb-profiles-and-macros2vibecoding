# Repository workflow

- Default workflow: Codex-managed local worktrees.
- Codex performs ordinary Git operations itself.
- Do not use the AgentLoop Owner Toolkit or AgentLoop Exchange for ordinary branch, commit, push, or pull-request operations.
- Base new tasks on a fresh `origin/main` unless explicitly instructed otherwise.
- Never work directly in the canonical anchor checkout.
- Branch creation, commits, pushes, and opening pull requests are permitted.
- Direct pushes to `main` are forbidden.
- Merge only with separate explicit authorization.
- Before publication, run relevant tests/builds and `git diff --check`.
- Host-level mutations, Codex configuration/hooks, live HID work, physical canaries, and changes to installed runtimes remain separate owner operations requiring explicit confirmation.
- Do not commit secrets, raw live captures, machine-specific paths, or private VOROTEX dumps.
