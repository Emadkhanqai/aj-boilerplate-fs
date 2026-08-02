# Model Routing Policy

**Every task starts by classifying the work and recommending the cheapest model that fits.**
Claude cannot switch its own model — so it must classify the task up front and, if the current
model is more expensive than the work needs, **stop and say so before continuing**.

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

## What to say

If the current model is more capable than the task needs:

> Recommended model: the workhorse tier. Please switch model before continuing.

If the task is high-risk or architectural:

> Recommended model: the frontier tier for this architecture/security review.

State the recommendation, then wait. Do not burn a frontier model on routine implementation
just because a session is already open on it.

## Operating rules

- **Keep sessions short.** Long sessions burn context and degrade output quality.
- **After every completed slice:** summarise, let the `session-handoff` hook record the state,
  and recommend closing the session.
- Subagents inherit this policy. The orchestrator assigns each dispatched agent the tier its
  task warrants.

## Why

Frontier models are for judgment-heavy work where a wrong call is expensive. The workhorse
tier handles the bulk of implementation at a fraction of the cost. Routing by task type keeps
spend proportional to risk.
