# ADR-0010: Direction switching without an i18n library — partial RTL, deliberately

**Status:** Accepted
**Date:** 2026-08-03
**Deciders:** Boilerplate maintainers
**Supersedes:** —

---

## Context

The workspace already had two thirds of a bilingual story and no way to tell which third was
missing.

`LanguageService` (`src/frontend/libs/shared/util/src/lib/language.service.ts`) holds an
`en`/`ar` signal and a `pick(en, ar)` helper, because API payloads carry paired `*En`/`*Ar` fields
— `FeatureAnnouncement` is the worked example — and something has to choose which one renders.
`DESIGN.md` §5 mandates logical properties (`margin-inline`, `inset-inline-start`,
`text-align: start`) precisely so "a future right-to-left locale flips correctly without a second
stylesheet", and `tokens.css` / `components.css` were written that way throughout. One rule in
`components.css` even carries an explicit `[dir='rtl']` branch for a `translateX` sweep.

What was missing was the single attribute that activates all of it. Nothing anywhere set
`document.documentElement.dir`. Selecting Arabic changed which half of a bilingual API field
rendered and nothing else: Arabic text laid out left-to-right, sidebar still on the left, trailing
action columns still on the physical right. The CSS was ready for a mirrored layout that could
never happen.

That is a bad state to leave a boilerplate in, in a specific way: it *looks* solved. A consuming
team reads `DESIGN.md` §5, sees the logical properties, sees `AppLanguage = 'en' | 'ar'`, and
reasonably concludes right-to-left is handled. It is not, and they would find out late.

The honest fix is small. The dishonest fix — adopting an i18n framework nobody asked for, on
behalf of a product that does not exist yet — is large, and picks a vendor for every downstream
project. This ADR is about doing the small one and being explicit about its edges.

## Decision

We will switch the document's `dir` and `lang` reactively from `LanguageService`, and we will
**not** add an internationalisation library.

- `DocumentDirectionService` (`libs/shared/util/src/lib/document-direction.service.ts`) holds one
  `effect` that writes `dir` (`rtl` for `ar`, `ltr` otherwise) and `lang` onto
  `document.documentElement` whenever the language signal changes.
- `provideDocumentDirection()` is wired in `apps/web/src/app/app.config.ts`. The explicit app
  initializer is load-bearing: the service is `providedIn: 'root'` and nothing injects it, because
  its whole job is a side effect. Without the initializer it would exist, be fully tested, and
  never run.
- `lang` is set alongside `dir`, always. It is what switches screen-reader pronunciation and what
  makes `:lang()` and hyphenation work. A page that mirrors while still claiming `lang="en"` is an
  accessibility defect, not a cosmetic one.
- A language toggle lives in the shell's top bar, so the capability is reachable rather than
  theoretical.
- Physical-direction CSS (`margin-left`, `padding-right`, bare `left:`/`right:`,
  `text-align: left|right`, `float`) stays banned in shared styles, as `DESIGN.md` §5 already
  says. One violation existed and was fixed: `table.list th.col-actions` used
  `text-align: right`, which would have pushed a row's action buttons to the wrong side of a
  mirrored table. It is now `text-align: end`.

### What this explicitly does NOT do

This is the part that matters, and it is the reason this ADR exists rather than a commit message.

- **UI strings are not translated.** There is no message catalogue, no `$localize`, no Transloco.
  Every label, heading, button, validation message and empty state stays in English in both
  directions. Switching to Arabic gives you an Arabic *layout* wrapped around English *text*.
- **Only data is bilingual**, and only where the API sends both halves (`pick(en, ar)`).
- **Dates, numbers and currency are not localised.** `formatDateTime` keeps one fixed
  presentation; there are no Arabic-Indic digits, no Hijri calendar, no locale-aware number
  grouping.
- **Bidirectional text is not handled beyond what the browser does for free.** This has a visible
  consequence, captured in the RTL screenshots: an English sentence inside an RTL paragraph gets
  its trailing full stop moved to the visual left — `.The sample feature. A server-paged…`. That is
  the Unicode bidirectional algorithm behaving exactly as specified for an LTR run inside an RTL
  paragraph. It is not a bug in the layout, and it is not worth patching with `<bdi>` wrappers per
  string: it disappears on its own once the strings are genuinely Arabic. It is left visible, and
  documented here, because it is the clearest possible demonstration of the boundary this ADR is
  drawing.
- **Locale is not persisted or negotiated.** No `localStorage`, no `Accept-Language`, no URL
  segment. A reload returns to English.

Call it *partial RTL*. It is the layout half of bilingualism, shipped honestly, with the language
half left to the consuming project.

### What a consuming project must do for real i18n

1. Pick a library — `@angular/localize` for compile-time extraction, or Transloco/`ngx-translate`
   for runtime catalogues. Runtime is usually the right answer for an app that switches language
   without a reload, which is what the toggle here implies.
2. Extract every hard-coded string in `apps/web` and `libs/**` into catalogues. There is no
   shortcut and it is the bulk of the work.
3. Delete `LanguageService.pick` and route bilingual API fields through the same catalogue
   mechanism, or keep `pick` for server-owned content only and be deliberate about the split.
4. Replace `formatDateTime` and any number formatting with locale-aware equivalents
   (`Intl.DateTimeFormat`, `Intl.NumberFormat`), and decide on calendar and digit shaping.
5. Persist and negotiate the locale (storage, URL, or user profile).
6. Keep `DocumentDirectionService` — it stays correct, it just needs its `directionFor` map
   extended for any further RTL locale (`he`, `fa`, `ur`).

## Consequences

### Positive

- Arabic renders in a mirrored layout, which is the difference between "unusable" and "usable but
  untranslated".
- The investment `DESIGN.md` §5 already made in logical properties now pays off and is *verified*
  rather than assumed — the e2e suite asserts the sidebar physically moves to the other edge, which
  a rule written with `margin-left` could never satisfy.
- `lang` switching means assistive technology announces Arabic content in Arabic.
- The gap between "bilingual-ready" and "internationalised" is now written down where someone
  planning a release will find it, instead of being discovered during a localisation sprint.
- Adding a real i18n library later is unaffected by this: nothing here has to be undone.

### Negative

- **A half-translated interface can read as more broken than an untranslated one.** English labels
  in a mirrored layout, with bidi punctuation artifacts, may look like a bug to anyone who does not
  know it is deliberate. Mitigated only by documentation — which is a weak mitigation, and the
  honest cost of stopping here.
- The toggle advertises a capability the product does not fully have. A consuming project shipping
  to real Arabic-speaking users must either finish the job or remove the toggle; leaving it as-is
  is the one option that is actually wrong.
- Two mechanisms now exist for language (`pick` for data, nothing for UI strings), and a future
  i18n library makes three until someone consolidates them.
- Direction is global state written directly to the DOM. Two independently-directioned regions on
  one page are not supported, and would need per-element `dir`.

### Neutral

- `DESIGN.md` §5's logical-property rule graduates from advisory to load-bearing: breaking it now
  breaks a passing test rather than a hypothetical future locale.
- `libs/shared/util` gains a service that touches the DOM. It remains UI-free in the sense the
  layout doc means (no components, no templates), but it is no longer purely computational.

### Follow-on work

- An ESLint or stylelint rule banning physical-direction properties in `apps/web/src/design/**`
  would make §5 mechanically enforced instead of grep-enforced. The grep is currently a manual step
  in review, which is exactly the kind of enforcement that decays.
- Extend `directionFor` if another RTL locale is added.
- Decide, per consuming project, whether the toggle ships.

## Alternatives considered

### Adopt Transloco (or `@angular/localize`) now

The complete answer, and rejected on scope rather than merit. A boilerplate that ships a message
catalogue ships *English* strings in a *chosen* library's format, and every consuming project
either adopts that choice or migrates away from it. The library decision belongs to a product with
actual locales and actual translators. Meanwhile the layout defect is real today and cheap today,
and fixing it does not constrain that later choice at all.

### Set `dir` once in `index.html` and leave it

Trivial, and wrong in the specific way that matters: it makes direction a build-time constant while
`LanguageService` presents language as a runtime signal. The two would disagree the moment anyone
used the toggle, which is worse than the original bug because it would appear to work.

### Mirror with a second RTL stylesheet, or a build-time flipper

The pre-logical-properties approach. Rejected because the CSS here already uses logical properties,
so a flipped stylesheet would be a second artifact to keep in sync with the first for no benefit —
and the one physical rule that existed was a defect to fix, not a reason to build a pipeline.

### Doing nothing

Defensible, since no consumer has asked for Arabic. Rejected because the workspace was already
*claiming* to be bilingual-ready in three places (`DESIGN.md` §5, `LanguageService`, the
`FeatureAnnouncement` payload shape) while being structurally incapable of it. The cheapest honest
options were to close the gap or to delete the claims; closing it costs one service.

## Verification

- `libs/shared/util/src/lib/document-direction.service.spec.ts` asserts `dir` *and* `lang` flip in
  both directions, and that the app initializer constructs the service when nothing injects it —
  the regression guard for "the service exists, is tested, and never runs".
- `libs/shell/src/lib/top-bar/top-bar.spec.ts` asserts the toggle drives the document attributes
  through the full chain.
- `apps/web-e2e/src/journeys/rtl-direction.spec.ts` is the real proof. It asserts *geometry*, not
  attributes: the sidebar's bounding box must move from the left half of the viewport to the right
  half. An attribute-only assertion would pass just as happily on a page whose CSS ignored `dir`
  entirely. It also asserts the document does not scroll horizontally at 390 px in RTL, and runs an
  axe scan in Arabic.
- The signal that this ADR needs superseding: the first time a consuming project needs a translated
  string. At that point the decision to skip an i18n library has stopped serving, and the follow-on
  list above becomes the work. This ADR is then superseded, not amended.
- The signal that it is being *violated*: any physical-direction property appearing in
  `apps/web/src/design/**`. `grep -nE '(margin|padding|border)-(left|right)|text-align:\s*(left|right)|float\s*:' apps/web/src/design/*.css`
  should stay empty.

## References

- [ADR-0003: PrimeNG is the only component library](0003-primeng-as-sole-component-library.md) —
  PrimeNG components honour `dir` themselves, which is a meaningful part of why this was cheap
- [ADR-0007: The "What's new" modal is bespoke markup](0007-bespoke-whats-new-modal.md) — the one
  component whose stylesheet is outside the shared sheets, and which carries its own
  `inset-inline-end`; its decorative confetti uses physical `left`/`right` and is deliberately left
  alone, being absolutely-positioned ornament with no reading order
- `src/frontend/DESIGN.md` §5 — the logical-properties rule this makes load-bearing
- `src/frontend/libs/shared/util/src/lib/language.service.ts` — the signal this reacts to, and its
  own note that it is not an i18n framework
