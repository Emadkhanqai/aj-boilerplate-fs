# Session handoffs

Files in this directory are written automatically by the **`session-handoff` hook**
(`.claude/hooks/`), which runs on Claude Code's `Stop` event — that is, when a session ends.

Each handoff is one file, `<session>.md`, recording what that session did.

## Why they exist

The guardrails in [the workflow](../workflow.md) require one task per session and fresh context
for each task. That is deliberate — stale context is how an agent ends up reasoning from
decisions that were superseded two hours ago. But it means the *useful* context also disappears
at the session boundary.

A handoff is the small, durable summary that survives: what was done, what was left undone, what
was tried and rejected, and what the next session needs to know. It is the note you would leave
a colleague taking over mid-task.

## What the hook writes

- What changed, and why
- The state at the end: green, red, or mid-task
- Decisions taken during the session, and options considered and rejected
- What is left, and the obvious next step
- Any drift it noticed — `CLAUDE.md` or an ADR that no longer matches the code

The hook **reports** drift. It never edits code and never blocks. If it cannot run — because a
tool is missing or a command failed — it degrades to a plain reminder rather than failing the
session. A guardrail that breaks your workflow gets disabled, and a disabled guardrail protects
nothing.

## These are committed

Handoffs are **tracked in git**, not ignored. They are project history: the record of how the
codebase got to its current state, including the paths that were tried and abandoned. That
record is frequently more useful six months later than the commit messages are.

Review them like any other file in a pull request. Correct one if it is wrong.

## Reading them

Newest first, when you are picking up work someone else — or a previous session — started:

```bash
ls -lt docs/handoff/
```

If a handoff contradicts `CLAUDE.md` or an ADR, `CLAUDE.md` and the ADR win. A handoff is a
record of a moment, not a statement of the current convention. If the contradiction means the
documentation is out of date, fix the documentation — that is condition 4 of
[the Definition of Done](../definition-of-done.md).

## What must never be in here

The same rule as everywhere else: **no secrets, no credentials, no connection strings, no tokens,
no personal data.** A handoff is a normal committed file in a public repository. If one contains
something it should not, remove it and rotate the value.
