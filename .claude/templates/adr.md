# ADR-<NNN>: <Title>

Copy this to `docs/adr/<NNN>-<short-title>.md`.

- **Status:** Proposed | Accepted | Superseded by ADR-<NNN>
- **Date:** <YYYY-MM-DD>
- **Deciders:** <names/roles>

## Context

What problem or decision is this? What constraints apply — the spec in `docs/specs/`, the
standards in `.claude/standards/`, the non-negotiable rules (migration-based schema, push
approval, the SonarQube gate), and the `CLOUD_PROVIDER` switch?

State the forces honestly, including the ones pulling the other way.

## Decision

The choice made, stated plainly in one or two sentences. Present tense, active voice:
"We use X." Not "It was decided that X might be used."

## Consequences

Positive, negative, and follow-ups. What this **locks in**, what it **leaves open**, and what
becomes harder. An ADR with only positive consequences has not been thought through.

## Alternatives considered

| Option | Why rejected |
|---|---|
| <option> | <reason> |

---

**Write an ADR when:** adopting or dropping a dependency, changing the database or cloud
provider, introducing a global store, breaking an API contract, changing the authorization
model, or reordering the middleware pipeline. If you find yourself explaining a past decision
twice, it needed an ADR.
