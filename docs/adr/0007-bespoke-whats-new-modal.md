# ADR-0007: The "What's new" modal is bespoke markup, a bounded exception to PrimeNG-only

**Status:** Accepted
**Date:** 2026-08-03
**Deciders:** Boilerplate maintainers
**Supersedes:** —

---

## Context

[ADR-0003](0003-primeng-as-sole-component-library.md) makes PrimeNG the only component library and
bans native form controls in feature code. It also names its own escape hatch: *"If PrimeNG
genuinely lacks something, build it once in `libs/shared/ui` on PrimeNG primitives … and write an
ADR if it is a significant pattern."* This is that ADR.

The ["What's new" feature spotlight](../whats-new.md) needed a presentation surface. Unlike every
other component in the workspace, its job is to **not** look like the product: it is the one moment
the application speaks about itself rather than about the user's data, and a panel that reads as
ordinary app chrome is a panel people dismiss without reading. The design that survived review is a
lavender-to-white gradient hero band with a bouncing spotlight glyph and drifting confetti, a stack
of tinted benefit cards, and a blurred, deliberately inert backdrop.

`p-dialog` was the obvious host, and ADR-0003 says to reach for it. Three parts of the design are
what it would have cost:

- **The gradient hero band.** `p-dialog` owns its header and content structure, and a full-bleed
  band that overruns the panel's rounded corners means fighting the theme layer's header, padding,
  and overflow rules — every one of which is a themed token another component also depends on.
- **The backdrop blur.** The mask is themed globally. Making this one dialog's mask blur means
  either a global change nobody asked for, or a per-instance override that reaches into PrimeNG's
  internals and breaks on their next release.
- **The inert backdrop.** Dismissal here is not a UI nicety: it writes a permanent, cross-device
  acknowledgement row, so a stray click outside the panel must not count as "I have read this".
  `p-dialog`'s dismissable-mask behaviour is a boolean, but the surrounding assumption — that a
  modal's mask is a close affordance — is baked into how the component is themed and documented,
  and quietly inverting it is worse than not using it.

Each of those is individually survivable. Together they meant the PrimeNG version would have been a
larger, more fragile pile of overrides than a self-contained component, while producing something
that must look nothing like the theme it was fighting.

## Decision

We will ship `WhatsNewModalComponent` (`src/frontend/libs/shared/ui/src/lib/whats-new-modal/`) as
bespoke markup with its own component stylesheet — a **named, bounded exception** to ADR-0003.

The exception's boundaries, all of which are enforceable by reading one directory:

- **Scoped to this one component.** Not to `libs/shared/ui`, not to modals in general. Everything
  else in the workspace stays PrimeNG-only and token-driven.
- **Icons still come from PrimeIcons** (`pi pi-times`, `pi pi-arrow-left`, `pi pi-arrow-right`).
  The exception covers layout and styling, not iconography.
- **Accessibility is explicitly not exempt.** The component carries `role="dialog"`, `aria-modal`,
  `aria-labelledby`, a labelled close button, and `role="tab"` / `aria-selected` pagination dots
  itself, precisely because PrimeNG is not there to supply them.
- **Its colour values stay in its own stylesheet**, never in `tokens.css`, `components.css`, or
  `app-preset.ts`. The palette must not be reachable by product chrome — keeping it local is what
  prevents that.
- **It is not precedent.** The next bespoke-looking request is PrimeNG-only until someone argues
  the case again, in a new ADR.

The decision is recorded in four places a developer or an agent will actually hit: the component's
class comment, `src/frontend/libs/shared/ui/README.md`, `src/frontend/DESIGN.md` §6, and
`src/frontend/CLAUDE.md` rule 3.

## Consequences

### Positive

- The spotlight looks like an announcement rather than a form, which is the entire point of the
  surface.
- No override layer sitting on top of PrimeNG's dialog theming — nothing here breaks on a PrimeNG
  minor release, and the component is legible in one directory.
- The backdrop's inertness is a plain empty method with a comment explaining it, rather than an
  inverted framework flag a reader must know to distrust.
- ADR-0003's escape hatch has now been exercised once, in the open, with its cost written down.
  That is a better outcome than an undocumented exception or a rule quietly weakened.

### Negative

- **There are now two styling idioms in this codebase.** Every other component reads tokens; this
  one carries literal colours. Anyone learning the workspace from this file learns the wrong
  pattern, which is why the exception is restated in four places and why the component's own
  comment leads with it.
- **The `anyComponentStyle` budget was raised twice** in `src/frontend/apps/web/project.json`:
  from 4 kB / 8 kB to 8 kB / 10 kB to admit the component at all, then to **10 kB / 14 kB** warn /
  error. The second raise is the instructive one. 8 kB had been set flush against a stylesheet that
  minified to ~7.8 kB, so the very next legitimate addition — the `prefers-reduced-motion` block
  this component was missing — exceeded it by ten bytes. A ceiling set to hug today's size is a
  tripwire, not a budget, and the choice it forces is between shaving semantic CSS and moving the
  number. The current figures carry deliberate headroom, and the production build is clean. That budget exists to flag components carrying
  too much bespoke CSS, and it was flagging correctly: this component genuinely is one. Raising it
  lowers the alarm for **every** component in the app, not just this one. That is a real loss of
  signal and the honest price of the exception.
- Accessibility here is hand-maintained. A future edit can regress the ARIA wiring in a way that no
  library upgrade would have caught for us; only the component's own tests do. This already caught
  us out **twice**, and both times the missing piece was one a component library ships by default.
  First, the stylesheet shipped seven `@keyframes` blocks with no `prefers-reduced-motion` branch —
  something a themed PrimeNG animation would have handled for us — contradicting the guarantee
  `DESIGN.md` makes. It now carries one, dropping entrances and stopping the ambient loops while
  leaving sub-200ms hover feedback alone. Second, `.wn-body` scrolls (`overflow-y: auto`) but was
  not focusable, so a keyboard-only user could not scroll it and never saw the rest of a long
  announcement — axe's `scrollable-region-focusable`, WCAG 2.1.1, severity *serious*. It now
  carries `tabindex="0"` with a resolved name. Note what these two have in common: neither was
  caught by review, both were caught by the automated suite, and both are things `p-dialog` would
  simply have been correct about. Treat that as the standing cost of this decision, not a one-off:
  every affordance a component library would have given you here is one you own, and you will
  discover which ones only when a checker tells you.
- If PrimeNG later ships something that fits, we will have a component nobody thinks to revisit.

### Neutral

- The stylesheet owns its own motion (seven `@keyframes` blocks) and its own responsive behaviour
  (a `@media (max-width: 480px)` branch that turns the panel into a bottom sheet), in a codebase
  where both are otherwise theming concerns.
- The body-parsing convention (`- 🔖 Title — description` becomes a card, anything else a
  paragraph) is a rendering decision the client owns. The API returns plain text and never
  interprets it, so a different client is free to render it differently.

### Follow-on work

- If a **second** component approaches the 10 kB warning, the answer is to question that component —
  not to raise the budget again. A third bespoke surface means ADR-0003 is no longer describing
  reality and needs superseding, not another exception.
- Revisit if PrimeNG gains a headless or slot-based dialog that leaves the panel's structure to the
  caller.

## Alternatives considered

### Build it on `p-dialog` and override the theme

The default, and the reason this ADR exists. Rejected on the three costs in the context above: the
gradient hero band, the blurred mask, and the inert backdrop each require reaching past the
component's own theming. The result would have been *more* custom CSS than the bespoke version, just
spread across override selectors coupled to PrimeNG's internal class names instead of contained in
one file — and every one of those overrides is a hostage to the next PrimeNG release.

### Skip the visual design; ship a plain themed dialog

Cheapest, and genuinely tempting. Rejected on what the surface is for: an announcement that looks
like the rest of the application is read as part of the application and dismissed reflexively. The
distinctiveness is the feature, not decoration around it.

### Put the bespoke values in the shared token set

Would have satisfied "no literal colours in a component" on a technicality. Rejected because it is
worse: this palette must **never** be reachable by product chrome, and a token is an invitation to
reuse. Locality is the control here.

### Keep the 4 kB / 8 kB budget and split the stylesheet

Splitting one component's CSS across several files to slip under a per-component budget is gaming
the metric, not meeting it. Rejected as dishonest instrumentation — it would have hidden exactly the
signal the budget exists to produce.

### Doing nothing — no spotlight at all

A real option: no announcement surface, and release notes elsewhere. Rejected because the backend
half of the module is small, domain-free, and repeatedly re-implemented per project; the value is in
having it solved once, and it needs *a* surface.

## Verification

- The exception is honoured while `whats-new-modal.css` is the **only** component stylesheet under
  `src/frontend/libs/`. `find src/frontend/libs -name '*.css'` should return exactly one path; a
  second one means this ADR has quietly become a policy.
- `npx nx build web --configuration=production` warns at 10 kB and fails at 14 kB per component
  stylesheet, and currently reports no budget warning at all. A warning on any *other* component is
  the signal to re-open ADR-0003, not the budget.
- The accessibility contract is asserted in
  `src/frontend/libs/shared/ui/src/lib/whats-new-modal/whats-new-modal.spec.ts`.

## References

- [ADR-0003: PrimeNG is the only component library](0003-primeng-as-sole-component-library.md) —
  the rule this bounds, including the escape hatch it names
- [docs/whats-new.md](../whats-new.md) — what the module does and why it exists
- `src/frontend/DESIGN.md` §6 · `src/frontend/libs/shared/ui/README.md` ·
  `src/frontend/CLAUDE.md` rule 3
