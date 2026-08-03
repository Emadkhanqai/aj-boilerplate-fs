# ADR-XXXX: <Short decision, stated as a fact>

**Status:** Proposed | Accepted | Deprecated | Superseded by [ADR-YYYY](YYYY-....md)
**Date:** YYYY-MM-DD
**Deciders:** <names>
**Supersedes:** — | [ADR-ZZZZ](ZZZZ-....md)

> One file per decision. Number them sequentially: `0008-short-slug.md`. Never rewrite history
> — a decision that turned out wrong gets a *new* ADR that supersedes it, and this one is marked
> `Superseded`. An ADR records why a choice was made at a point in time; that record stays true
> even when the choice stops being.
>
> Write an ADR when a decision is expensive to reverse, affects more than one team or layer,
> constrains future work, or when you can already imagine someone asking "why on earth is it
> like this?" six months from now. Do not write one for a choice a single pull request can undo.

---

## Context

What forces are at play? The constraints, the requirements, the state of the system, the
deadline, the team's experience, the thing that broke last time. Enough that a reader arriving
in two years understands the situation without needing to have been there — and no persuasion.
State the problem so fairly that someone who prefers a rejected alternative would agree the
description is accurate.

## Decision

We will <do the thing>.

State it in the active voice, as a commitment, in a sentence or two. Then add the detail that
makes it actionable: what exactly is in scope, what the rule is, and where it is enforced
(a test, a lint rule, a CI gate — enforcement beats intention).

## Consequences

### Positive

- <what this makes easier, faster, safer, or cheaper>

### Negative

- <the real cost — be honest; an ADR with no negative consequences is marketing>

### Neutral

- <what changes without being better or worse: new conventions, new files, things people must
  now remember>

### Follow-on work

- <what this decision obliges us to do next>

## Alternatives considered

### <Alternative A>

What it was, and specifically why it lost. "It was worse" is not a reason; name the trade-off.

### <Alternative B>

Same again.

### Doing nothing

Almost always a real option. Say why it was rejected.

## Verification

How would we know this decision is being honoured, and how would we know it has stopped
serving us? Name the test, gate, or metric — and the signal that should trigger a superseding
ADR.

## References

- <spec, issue, benchmark, external documentation>
