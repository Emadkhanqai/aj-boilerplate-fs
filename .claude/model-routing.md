# Model Routing Policy

**Every task starts by classifying the work and recommending the cheapest model that fits.**
Claude cannot switch its own model — so it must classify the task up front and, if the current
model is more expensive than the work needs, **stop and say so before continuing**.

This policy is **enforced, not suggested**. The [`model-routing`](hooks/model-routing.sh) hook
runs on the `UserPromptSubmit` event, which injects its stdout into the model's context, so the
rules below are placed in front of the model on **every prompt of every session**, including
inside subagents. There is no path where it is simply forgotten.

---

## The trigger — when classification happens

> **BEFORE the first tool call and BEFORE the first code edit of a task.**

Not after reading the files. Not after the plan. Not "once it becomes clear how big this is".
Classification is the first thing that happens, because its whole purpose is to stop expensive
work *before* the expense is incurred. A recommendation delivered after the work is done has
cost exactly what it was meant to save.

And it is **stated to the user, out loud, in the first reply** — never assumed silently, never
recorded only in reasoning the user cannot see. The point is to give the human the decision.

### What to say, in the first reply

One line, then act:

```
Task: <task type> — cheapest sufficient tier: <FRONTIER|WORKHORSE>.
```

- **Session already on the right tier** → add a single clause ("this session matches, carrying
  on") and get to work. No ceremony, no paragraph about routing.
- **Session on a costlier model than the work needs** → **STOP.** Do not start the work.

  > Recommended model: the workhorse tier — this is routine implementation. Please switch model
  > before continuing.

  Then wait. Claude cannot switch its own model; only the user can.
- **Task is high-risk or architectural and the session is on a cheap tier** →

  > Recommended model: the frontier tier for this architecture/security review.

  Say so before producing a judgement the tier cannot support.
- **Task spans both** (e.g. "design it, then build it") → route by the *next* step, not the
  whole arc. Recommend frontier for the design, then recommend dropping to workhorse for the
  build, at the point the design is agreed.

---

## Classify first

| Task type | Recommended tier |
|---|---|
| Architecture / design decisions | **Frontier** (Opus-class) |
| Security review / threat modelling | **Frontier** |
| Complex debugging (multi-system, root cause unknown) | **Frontier** |
| High-risk refactors (broad blast radius, hot paths) | **Frontier** |
| Final pre-push review | **Frontier** |
| Normal implementation / CRUD | **Workhorse** (Sonnet-class) |
| Tests (unit / integration / architecture / component) | **Workhorse** |
| Frontend build / UI | **Workhorse** |
| Database migrations | **Workhorse** |
| Static-analysis fixes | **Workhorse** |
| Docs / config updates | **Workhorse** |
| Routine search / file lookup / status reporting | **Workhorse** |

**When two rows fit, take the cheaper one** unless a wrong answer is expensive to reverse. That
is the whole test: not "is this hard?" but "what does it cost to be wrong?"

---

## Operating rules

- **Keep sessions short.** Long sessions burn context and degrade output quality.
- **After every completed slice:** summarise, let the `session-handoff` hook record the state,
  and recommend closing the session.
- **Subagents inherit this policy.** The orchestrator assigns each dispatched agent the tier
  *its* task warrants — a frontier session dispatching three test-writing agents should be
  dispatching them at the workhorse tier, not its own.
- **Do not re-announce on every follow-up turn.** Classify once per task. Re-state only when
  the task type changes — when a build turns into a debugging session, or a review turns into
  a refactor.

## Opting out

`AJ_SKIP_MODEL_ROUTING_HOOK=1` silences the hook's per-prompt reminder. The policy still
applies; only the automatic nudge stops. Set it if you have internalised the routing and want
the context back.

## Why

Frontier models are for judgment-heavy work where a wrong call is expensive. The workhorse
tier handles the bulk of implementation at a fraction of the cost. Routing by task type keeps
spend proportional to risk.

The reason this is a hook rather than a paragraph: a rule that depends on the model remembering
to read a linked file is a rule that holds right up until the session gets busy — which is
exactly when the expensive work happens. Prose is advisory; a hook is deterministic.

## Related

[`hooks/model-routing.sh`](hooks/model-routing.sh) · [`README.md`](README.md) ·
[`../CLAUDE.md`](../CLAUDE.md)
