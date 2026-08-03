<!--
  Delete nothing from this template. If a section does not apply, write "n/a" and
  one line saying why — that is information. A silently removed section reads as
  an oversight, and a reviewer has to ask.

  The Definition of Done block below is not decoration. It is copied from
  docs/definition-of-done.md and it is checked at review time, which is the only
  time a checklist has ever changed an outcome.
-->

## What and why

<!--
  Prose, not a list of commits. The commit log already exists and nobody reads it
  for meaning. Explain what changed, and — more importantly — why this change and
  not one of the alternatives.

  A reviewer who understands the intent can spot a wrong implementation. A reviewer
  who only sees the diff can only spot a wrong line.
-->

**Spec:** `docs/specs/____`
**ADR:** <!-- number and title, or "none needed — a single PR can undo this" -->

---

## Type of change

<!--
  Tick one. These are the Conventional Commits types this repository has adopted;
  the full convention, including scopes and the breaking-change marker, is in
  CONTRIBUTING.md. The pull request title should use the same type.
-->

- [ ] `feat` — a new capability
- [ ] `fix` — a defect corrected
- [ ] `docs` — documentation only
- [ ] `refactor` — behaviour unchanged, structure changed
- [ ] `perf` — behaviour unchanged, performance changed
- [ ] `test` — tests only
- [ ] `build` — build system, dependencies, packaging
- [ ] `ci` — workflows and pipeline configuration
- [ ] `chore` — housekeeping that touches no shipped behaviour
- [ ] `revert` — reverts a previous change

- [ ] This is a **breaking change** <!-- if ticked, describe the break and the migration path under "Risk and rollback" -->

<!--
  The links in this template use repository-root paths with a leading slash. That form
  resolves correctly both when this file is browsed in `.github/` and when it is
  rendered into a pull request body, where the file's own directory is not the base.
-->

See [CONTRIBUTING.md](/CONTRIBUTING.md) for the commit convention in full.

---

## How to verify

<!--
  The exact commands a reviewer should run, in order, copy-pasteable. Not "run the
  tests" — the command, with the project path and any configuration flag.

  If verification needs a database, a running API, or a seeded row, say so and say
  how to get one. A reviewer who has to reconstruct your setup will skip the step
  and approve on reading alone, which is how defects reach staging.
-->

```bash

```

**Manual steps, if any:**

---

## Test evidence

<!--
  Paste the output. Not a claim that it passed — the terminal text.

  Definition of Done condition 1 is explicit about this: a criterion with no test is
  not met, and a test that has never been observed failing has not been shown to
  test anything. If you wrote the test after the code, say so, and say how you
  confirmed it fails without the change.

  Map each acceptance criterion in the spec to the test that covers it.
-->

| Acceptance criterion | Test that covers it |
|---|---|
|  |  |

<details>
<summary>Test output</summary>

```

```

</details>

---

## Screenshots

<!--
  REQUIRED for any user-visible frontend change. Not required otherwise — write
  "n/a — no user-visible change" and move on.

  Before and after, at the viewport the change matters at. If the change involves
  a state a screenshot cannot show — a transition, a loading state, an empty state,
  an error state — capture each one, or record it.

  A description of what a screen looks like is not evidence of what it looks like.
-->

| Before | After |
|---|---|
|  |  |

---

## Risk and rollback

<!--
  Answer all four. "Low risk" on its own is not an answer; it is a feeling.
-->

**What could this break, including things it does not obviously touch?**

**How would we notice — which log, metric, alert, or user report?**

**How do we roll it back?** <!-- Revert the merge commit? Is that enough? A migration, a
config change, or a consumed API contract may make the revert insufficient on its own. -->

**Does anything have to happen in a particular order at deploy time?** <!-- migration before
code, feature flag first, a dependent service released first -->

---

## Definition of Done

<!--
  Copied verbatim from docs/definition-of-done.md. Six conditions. Not five. There
  is no partial credit and no "done except for".

  Read docs/definition-of-done.md for the full text of each condition — the one-line
  summaries below are reminders, not the rule.
-->

- [ ] 1. Every acceptance criterion met, with test output pasted below
- [ ] 2. All CI gates green; quality gate passed (0 new Blocker/Critical/Major, ≥80% new-code coverage)
- [ ] 3. `/review` findings resolved AND a human has approved
- [ ] 4. CLAUDE.md / ADR / OpenAPI + generated types updated, or confirmed unchanged
- [ ] 5. Deployed to staging and smoke-tested — what was tested: ________
- [ ] 6. No open critical or major findings

Spec: docs/specs/____
Evidence:

<!--
  Note on condition 5: "merged is not deployed, and deployed is not working." A health
  check is not a smoke test. Exercise the changed path itself, on staging, and write
  down what you exercised and what happened.

  Note on condition 6: an open critical finding with a follow-up ticket attached is
  still an open critical finding.
-->

Full text: [docs/definition-of-done.md](/docs/definition-of-done.md)

---

## If an agent wrote any of this

An agent-written change gets **more** scrutiny, not less.

Agent-written code is fluent, confident, plausible, and consistently formatted, which makes a
wrong approach look exactly like a right one. Reviewers should be most careful precisely where
the code reads most smoothly. This is condition 3 of the Definition of Done, and it is not
waived because the diff is clean.

The developer who prompted it **owns every line of it** and must be able to explain any line on
request. If you cannot explain a line, it does not merge — go and understand it, or delete it.

- [ ] An agent wrote part or all of this change
- [ ] I have read every line and can explain each one
- [ ] `/review` has run and its findings are resolved
