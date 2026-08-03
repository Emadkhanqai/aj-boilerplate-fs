# DESIGN.md — the visual contract

**This is a TEMPLATE. Fill it in before any UI is built.**

Every agent and every developer reads this file before writing a component. It is the answer to
"what should this look like?", so that the answer is never "whatever the last person did". An
unfilled DESIGN.md produces an application that looks like it was assembled by strangers —
because it was.

Keep it short enough that it is actually read. If a rule here is ever violated by shipped UI,
either fix the UI or change the rule — never leave the two disagreeing.

## How this file relates to the code

| This file says | The code enforces it in |
|---|---|
| Colour, spacing, radius, shadow, type scale | `apps/web/src/design/tokens.css` (CSS custom properties) |
| Component appearance (buttons, panels, tables, forms) | `apps/web/src/design/components.css` |
| PrimeNG component theming | `apps/web/src/styles/app-preset.ts` |

Those three files are the ONLY places visual values may live. A colour hard-coded in a component
is a bug, not a shortcut. When you change a value here, change it in the matching file in the same
commit.

---

## 1. Brand and tone

> Replace this section.

- **Product name:**
- **What it is, in one sentence:**
- **Who uses it, and in what state of mind:** (e.g. "finance reviewers, under deadline, reading
  dense tables" — this decides density and contrast far more than any colour choice)
- **Three adjectives the interface should earn:** (e.g. calm, precise, unfussy)
- **Three it must avoid:**

## 2. Colour

Fill in the values, then mirror them into `tokens.css` and `app-preset.ts`.

| Token | Value | Used for |
|---|---|---|
| `--ink` | | Primary text |
| `--paper` | | App background |
| `--panel` | | Card / panel surface |
| `--line` | | Hairline borders |
| `--brand` (`--moss` today) | | Primary actions, active nav, links |
| `--brand-deep` | | Pressed/active states, dark surfaces |
| `--muted` | | Secondary text |
| `--danger` | | Destructive actions, errors |
| `--info` | | Informational states |
| `--amber` | | Warnings, pending states |

**Rules**

- Contrast: body text ≥ 4.5:1, large text and UI boundaries ≥ 3:1 (WCAG 2.2 AA). The axe-core
  suite in `apps/web-e2e` fails the build on violations — do not disable a rule to make it pass.
- Colour never carries meaning alone. Every status pill has text; every error has a message.
- One primary colour. If a second "brand" colour appears, decide which one wins and delete the
  other.

## 3. Typography

| Role | Family | Size | Weight | Line height |
|---|---|---|---|---|
| Page title (`h1`) | | | | |
| Section heading (`h3`) | | | | |
| Body | | 14px | 400 | 1.5 |
| Small / caption | | 12px | | |
| Numeric / code | mono | | | `font-variant-numeric: tabular-nums` |

**Rules**

- One sans family, one mono family. No third.
- Numbers in tables use the `.tabular` class so columns align digit-for-digit.
- Never centre a paragraph of body text.

## 4. Spacing, radius, elevation

- Spacing scale: 4px base — `--space-1` (4) through `--space-12` (48). Use the tokens; do not
  invent `13px`.
- Corner radius: `--radius` (10px) for panels and dialogs; 8px for inputs and buttons; 20px for
  pills. Three values, no more.
- Elevation: `--shadow` for resting surfaces, `--shadow-lift` for overlays. Nothing else casts a
  shadow.
- Control height: every single-line input, select, and button is `--control-h` (40px) tall, so a
  button beside a field lines up exactly.

## 5. Layout

- Max content width: **fill in** (the shell's `.content` sets it).
- Sidebar width: `--side-w` (264px).
- Breakpoints: **fill in** — the shell collapses the sidebar below one of them.
- Use logical properties (`margin-inline`, `padding-inline`, `text-align: start`) everywhere, so
  a future right-to-left locale flips correctly without a second stylesheet.

## 6. Component rules

**PrimeNG only.** No bare `<button>`, `<input>`, `<select>`, `<textarea>`, or `<table>` in any
template. Use `p-button`, `pInputText`, `p-select`, `pTextarea`, `p-table`. This is not
stylistic — it is how focus rings, disabled states, keyboard behaviour, and ARIA wiring stay
consistent without anyone having to remember them.

> **Documented exception — `app-whats-new-modal`** (`libs/shared/ui/src/lib/whats-new-modal`).
> The "What's new" feature spotlight is bespoke markup with its own literal colour values and
> seven keyframe animations, kept in a component stylesheet rather than in `tokens.css` /
> `components.css`. That is deliberate on both counts: it is a one-off announcement surface that
> must deliberately *not* read as product chrome, so its palette is precisely what should never
> enter the shared token set, and no PrimeNG component renders it. The exception is bounded to
> this one component and does not cover accessibility — it carries `role="dialog"`, `aria-modal`,
> `aria-labelledby`, a labelled close button, and `role="tab"` dots itself, because PrimeNG is
> not there to supply them. Everything else stays PrimeNG-only and token-driven.
>
> This exception also costs one build-config change worth knowing about: the component's
> stylesheet minifies to ~7.8 kB, so `apps/web/project.json`'s `anyComponentStyle` budget was
> raised from 4 kB / 8 kB to **8 kB / 10 kB**. That budget exists to flag components carrying too
> much bespoke CSS, and it was flagging correctly — this component genuinely is that. If a
> *second* component ever approaches the new ceiling, the answer is to question that component,
> not to raise the budget again.

| Pattern | Rule |
|---|---|
| Buttons | Exactly one primary action per view. Everything else is `secondary` or `text`. Destructive actions use `severity="danger"` and are always confirmed. |
| Dropdowns | Searchable (`[filter]="true"`) and sorted A–Z by label (`sortByLabel`) by default. Opt out only for a genuine conventional order, and say why in a comment. |
| Tables | Server-side paging and search for anything that can exceed one page. Right-align numeric columns. Sticky header for long tables. |
| Forms | Typed reactive forms. Validation messages appear under the field, after touch, with `role="alert"` and `aria-describedby`. Never rely on a toast to report a field error. |
| Dialogs | The app's own `app-confirm-dialog`. Never `window.confirm()` or `window.alert()`. |
| Destructive confirmation | Name the thing being destroyed in the message. "Delete item?" is worse than "Delete 'Q3 forecast'?". |
| Toasts | Errors and background successes only. Never for a field validation error, never for something the user is already looking at. |

## 7. The four states

**Every data view handles all four.** A view that only handles "success" is unfinished, and it is
the single most common defect in generated UI.

| State | What the user sees |
|---|---|
| Loading | A skeleton or an explicit "Loading…" with `role="status"`. Never a blank panel. |
| Error | An inline block with what failed, in plain language, plus a retry. Never a bare stack trace, never silence. |
| Empty | `app-empty-state` — what would be here, and the action that creates the first one. Distinguish "no data yet" from "no results for this filter". |
| Success | The content. |

See `libs/feature-items/src/lib/item-list-page/item-list-page.html` for all four in one file.

## 8. Accessibility (non-negotiable)

- Every interactive element is reachable and operable by keyboard, in a sensible order.
- Every icon-only control has an `ariaLabel`.
- Every input has a `<label for>`.
- Focus is visible — `:focus-visible` is styled globally in `tokens.css`; do not remove it.
- Live regions (`role="status"` / `role="alert"`) announce async outcomes.
- Respect `prefers-reduced-motion`; every animation in `components.css` already does.

## 9. Writing (microcopy)

- Sentence case for headings and buttons. Not Title Case, not ALL CAPS.
- Buttons are verbs: "Save changes", not "Submit".
- Errors say what happened and what to do next. "Someone else changed this record — reload to see
  their version" beats "Error 409".
- No exclamation marks. No "Oops!".
- Dates render through `formatDateTime` so they are consistent, and every surface showing times
  states the timezone (`DISPLAY_TIME_ZONE` in `libs/shared/util`).

## 10. Checklist before a UI change is done

- [ ] Values come from tokens, not literals.
- [ ] PrimeNG components only; no native form controls.
- [ ] All four states handled.
- [ ] Keyboard-navigable; labels and `ariaLabel`s present.
- [ ] Contrast checked; axe suite passes.
- [ ] Copy follows §9.
- [ ] Looks correct at the narrowest supported width, not just on a wide monitor.
