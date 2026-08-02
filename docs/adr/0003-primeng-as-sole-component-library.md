# ADR-0003: PrimeNG is the only component library, and native form controls are banned

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Boilerplate maintainers

---

## Context

Frontends decay through accumulation. A project starts with one component library, then adds a
second for a date picker it lacks, then hand-rolls a third pattern for a table, and within a
year the same concept — a dropdown — exists in four visually and behaviourally distinct forms.
Accessibility regresses where the hand-rolled ones are, keyboard behaviour diverges, and theming
becomes a per-component negotiation.

This is not primarily a taste problem, it is a consistency and accessibility problem, and it is
almost entirely preventable by making the rule absolute rather than a default.

The stack is Angular 21 with standalone components and signals. PrimeNG covers the full
enterprise-form surface — inputs, selects, autocomplete, date and time pickers, data tables with
sorting/filtering/paging/virtual scroll, dialogs, toasts, trees, file upload — with keyboard and
ARIA support already in place, and a theming system driven by design tokens.

## Decision

We will use PrimeNG as the **only** component library, and native HTML form controls are banned
in feature code.

- No `<input>`, `<select>`, `<textarea>`, `<button>`, or `<table>` in a feature template. Use
  `p-inputtext`, `p-select`, `p-textarea`, `p-button`, `p-table`.
- Structural and layout HTML — `<div>`, `<section>`, `<nav>`, `<h1>`, `<p>` — is of course fine.
  The ban is on *controls*.
- Dropdowns are **filterable and sorted A–Z by default**. A user should never have to hunt an
  unsorted list, and should never have to scroll one they could type into.
- No second component library. Not "just for this one widget".
- If PrimeNG genuinely lacks something, build it once in `libs/shared/ui` on PrimeNG primitives,
  with the same theming tokens and the same accessibility expectations — and write an ADR if it
  is a significant pattern.
- Theming goes through PrimeNG design tokens. No per-component colour overrides scattered
  through feature styles.

## Consequences

### Positive

- Every control in the product behaves the same way: same keyboard handling, same focus ring,
  same validation display, same loading state.
- Accessibility is inherited rather than re-earned per component.
- Theming is one place. A brand change is a token change.
- Reviews get shorter. "Use `p-select`" is not a debate.
- Agents generating UI have exactly one correct pattern to follow, which markedly reduces the
  plausible-but-wrong output that a permissive rule invites.

### Negative

- We are coupled to one supplier's roadmap, release cadence, and breaking changes. A PrimeNG major
  upgrade is a whole-frontend event.
- Bundle size is larger than hand-rolled controls would be, though tree-shaking and per-component
  imports mitigate it.
- Occasionally a PrimeNG component is the wrong shape for a requirement and the workaround costs
  more than a purpose-built control would have.
- A hard rule will sometimes be wrong. We accept a small number of awkward cases in exchange for
  a rule that cannot erode by a thousand small exceptions.

### Neutral

- `libs/shared/ui` becomes the home for composed PrimeNG patterns, and needs its own review
  discipline so it does not become a dumping ground.
- `src/frontend/DESIGN.md` ships as a template the consuming project fills in before UI work, so
  tokens and layout rules are agreed before components are built.

### Follow-on work

- An ESLint rule flagging native control elements in feature templates would make this
  mechanically enforced rather than review-enforced.

## Alternatives considered

### Angular Material

Comparable quality and a first-party feel. Rejected on data-table capability: the enterprise
grid features this boilerplate's audience needs — column filtering, frozen columns, virtual
scroll, export — are built into PrimeNG's table and require substantial custom work on Material.

### A headless library plus our own styling

Maximum design freedom. Rejected: it converts a component-library decision into a design-system
project, which is a poor default for a team's first week and a poor fit for a boilerplate that
cannot know the consuming brand.

### Tailwind and hand-rolled components

Rejected for the same reason, plus the accessibility burden lands entirely on the team.

### "PrimeNG by default, native where it is simpler"

The tempting middle. Rejected because it is the exact policy that produces four kinds of
dropdown. A default is not a rule.

## Verification

Grep feature templates for native control tags — the result should be empty. Any second UI
dependency appearing in `package.json` is a violation of this ADR.

## References

- `.claude/standards/` — frontend standards
- `src/frontend/DESIGN.md`
