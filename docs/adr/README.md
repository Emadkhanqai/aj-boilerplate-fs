# Architecture Decision Records

An ADR records **why** a decision was made, at the time it was made. It is not documentation of
how the system works today — that is what `CLAUDE.md` and the standards are for. An ADR stays
true even after the decision is reversed, because it describes a moment.

## Index

| # | Decision | Status |
|---|---|---|
| [0001](0001-layered-clean-architecture.md) | Layered Clean Architecture, not vertical slices | Accepted |
| [0002](0002-dual-cloud-provider-behind-one-switch.md) | Two cloud providers behind a single `CLOUD_PROVIDER` switch | Accepted |
| [0003](0003-primeng-as-sole-component-library.md) | PrimeNG is the only component library | Accepted |
| [0004](0004-openapi-generated-frontend-types.md) | Frontend API types are generated from OpenAPI | Accepted |
| [0005](0005-apiresponse-envelope-and-status-code-contract.md) | Uniform `ApiResponse<T>` envelope and status-code contract | Accepted |
| [0006](0006-three-repository-split.md) | Publish as three repositories derived from one tree | Accepted |
| [0007](0007-bespoke-whats-new-modal.md) | The "What's new" modal is bespoke markup, a bounded exception to PrimeNG-only | Accepted |
| [0008](0008-no-audit-trail-or-soft-delete.md) | No audit trail and no soft delete — the ingredients ship, the policy does not | Accepted |
| [0009](0009-idempotency-keys-for-unsafe-requests.md) | `Idempotency-Key` is opt-in, POST-only, and replays a buffered response from a unique-indexed table | Accepted |
| [0010](0010-partial-rtl-without-an-i18n-library.md) | Direction switching without an i18n library — partial RTL, deliberately | Accepted |
| [0011](0011-scripted-one-way-derivation-for-the-three-repositories.md) | Derivation of the single-stack repositories is a committed one-way script | Accepted |

These eleven record the decisions taken when this boilerplate was built. Keep them as history and
start your own series at the next free number, or delete them and start at `0001` — but pick one
and be consistent.

**Numbers are never reused or renumbered.** Renumbering an ADR after the fact breaks every
cross-reference that already points at it, and a gap in a sequence is cheaper than a number that
means two different things in two repositories. If a number is claimed and the ADR is then
abandoned, leave the gap and say so here.

Three pairs are worth reading together:

- **`0003` and `0007`** — `0007` bounds `0003` rather than superseding it. It is the one place the
  PrimeNG rule bends, and it says exactly how far.
- **`0006` and `0011`** — `0006` decided to publish three repositories and named the drift risk;
  `0011` is the mechanism, written after the drift it predicted had already happened.
- **`0001` and `0008`** — `0008` is what the layering in `0001` deliberately does *not* buy you.

## Writing one

1. Copy [TEMPLATE.md](TEMPLATE.md) to `NNNN-short-slug.md`, using the next free number.
2. Fill in context, decision, consequences (including the negative ones), and the alternatives
   you actually considered.
3. Open it as its own pull request, or alongside the change it governs.
4. Set the status to `Accepted` when it merges.

**Never edit an accepted ADR to reflect a new decision.** Write a new one, mark it as superseding
the old, and set the old one's status to `Superseded by ADR-NNNN`.

## When to write one

Write an ADR when the decision is expensive to reverse, crosses team or layer boundaries,
constrains future work, or will provoke "why is it like this?" from someone who was not there.

Do not write one for a choice a single pull request can undo.

Reading the most recent five is part of [Day-1 onboarding](../onboarding.md).
