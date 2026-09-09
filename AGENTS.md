# Repository workflow

## Codex-native workspace baseline

- Default implementation workspace is a Codex-managed local worktree.
- A per-task fresh clone is not required. Fresh clone is a provisioning, recovery, or explicit task-specific operation.
- Base new tasks on a fresh `origin/main` unless explicitly instructed otherwise.
- Before mutation, prove the expected repository/origin, task scope, expected base when supplied, Git worktree membership, and that the current directory is the assigned worktree rather than the canonical anchor checkout.
- Detached HEAD is valid for inspection, editing, testing, and review.
- Before the first commit, create or switch to the approved dedicated task branch. Default prefix is `agent/` unless the task specifies another approved branch convention.
- Never work directly in the canonical anchor checkout or default branch.
- Codex performs ordinary Git operations itself: branch creation, staging, commits, pushes, and opening pull requests when publication is authorized by the task/repository policy.
- Windows sandbox protection of Git metadata is intentional. If a Git metadata mutation (`git add`, commit, or ref/index operation) is blocked at the sandbox boundary, Codex must use native approval/escalation for that exact Git operation; approval is not an infrastructure failure. If the native path is unavailable or the approved exact operation still fails, stop and report.
- Do not widen ACLs, use `takeown`, run Codex elevated, add `.git` to broad writable roots, or switch ordinary repository Git work to AgentLoop Owner Toolkit.
- Do not use the AgentLoop Owner Toolkit or AgentLoop Exchange for ordinary branch, commit, push, or pull-request operations.
- Do not use stash, reset, clean, force push, or history rewrite to conceal unexpected workspace state.
- If repository/worktree/base/task identity cannot be proven, stop fail-closed.

## Publication and checks

- Branch creation, commits, pushes, and opening pull requests are permitted when the current task/repository policy grants publication authority.
- Direct pushes to `main` are forbidden.
- PRs are Ready for Review by default unless the task explicitly requires Draft.
- Merge authority is separate from implementation/publication authority. Merge only with separate explicit authorization.
- Before publication, run relevant tests/builds and `git diff --check`.

## Host and security boundary

- Host-level mutations, Codex configuration/hooks, live HID work, physical canaries, and changes to installed runtimes remain separate owner operations requiring explicit confirmation.
- Do not commit secrets, raw live captures, machine-specific paths, or private VOROTEX dumps.
