# Standard: Git Approval Policy

**Status:** Enforced · **Applies to:** every agent and contributor.

This is the most important operational rule in the harness. It exists because pushing code has
consequences that are hard to reverse: CI triggers, deployments, shared history, and external
visibility.

## The rule

> **Never push without explicit user approval — every time.**

- No `git push` to any branch or any remote until the user explicitly authorises *that
  specific push*.
- Approval is **not durable.** Approval to push once is not approval to push again. Ask again
  for the next push.
- Approval for one branch or remote does not transfer to another.
- **"Commit" is ordinary local work; "push" is the gated action.**

## What agents MAY do without asking

- `git status`, `git diff`, `git log`, `git add`, `git commit`
- Create and switch local branches
- Stage and organise work locally

## What agents MUST NOT do without explicit, current approval

- `git push` in any form — including `--force`, `--force-with-lease`, `--tags`,
  `--set-upstream`
- Open or update a pull request that triggers remote CI
- Any command that publishes local state to a remote

`git push --force`, `--force-with-lease`, `git reset --hard`, and `git clean -fdx` are
additionally blocked outright by the [`block-dangerous`](../hooks/block-dangerous.sh) hook —
approval does not unblock them, because rewriting shared history is a human decision made
deliberately outside an agent session.

## Preconditions for even *proposing* a push

A push may be proposed only after **all** of the following are true:

1. The working tree is committed and clean — no stray changes.
2. Tests pass — see [`testing.md`](testing.md).
3. The **SonarQube scanner has run** on the current state — see [`sonarqube.md`](sonarqube.md).
4. **Zero open Blocker, Critical, or Major** SonarQube issues.
5. A short summary of what will be pushed has been presented to the user.

Only then may the agent ask: *"Ready to push `<branch>` to `<remote>`. Approve?"* — and wait
for an explicit yes.

## Branch discipline

- **Never commit feature work directly to `main`.** Branch first.
- Branch naming: `feature/<short-desc>`, `fix/<short-desc>`, `chore/<short-desc>`.
- Commit messages: imperative mood, scoped, explaining the *why*. One logical change per
  commit.
- **Never add AI or assistant attribution** — no "Co-Authored-By", no "Generated with …", no
  tool credit — in commits, pull requests, documentation, or code. Anywhere. Ever.

## Related

[`sonarqube.md`](sonarqube.md) · [`../commands/pre-push.md`](../commands/pre-push.md) · [`../workflows/pre-push-quality-gate.md`](../workflows/pre-push-quality-gate.md) · [`../workflows/release.md`](../workflows/release.md) · [`../hooks/block-dangerous.sh`](../hooks/block-dangerous.sh)
