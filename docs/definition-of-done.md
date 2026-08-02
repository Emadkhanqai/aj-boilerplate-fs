# Definition of Done

A change is **done** when all six conditions below are true. Not five. There is no partial
credit, and there is no "done except for".

"Done" here means: merged, deployed to staging, verified there, and safe to leave alone.

---

## 1. The spec's acceptance criteria are demonstrably met, with test evidence in the pull request

Every acceptance criterion in the spec maps to at least one test, and that test's passing output
is in the pull request. Not a claim that it passes — the output.

A criterion with no test is not met. A test that has never been observed failing has not been
shown to test anything.

## 2. All CI gates are green and SonarQube has passed

Every workflow green: build with warnings as errors, format verification, lint, unit tests,
integration tests, architecture tests, E2E where a journey changed, dependency vulnerability
scanning, secret scanning, and code scanning.

The quality gate specifically: **zero new Blocker, Critical, or Major findings** and **≥80%
coverage on new code**. Minor and Info findings may be triaged, with the triage recorded.

A red gate is not "nearly done" and is never merged with a follow-up ticket attached.

## 3. AI review and human review are both approved

`/review` has run and its findings are resolved. Then a human has read every line and approved.

Both. In that order. **Human review is mandatory and is never waived because an agent wrote the
code** — agent-written code is fluent and confident, which makes a wrong approach look like a
right one, so it warrants more scrutiny rather than less. The developer who prompted it owns it
and must be able to explain any line of it.

## 4. `CLAUDE.md`, the ADRs, and the OpenAPI document are updated if conventions or contracts changed

- A convention changed → `CLAUDE.md` changes in the same pull request.
- A decision was made that is expensive to reverse → an ADR lands with the change.
- An API contract changed → the OpenAPI document is updated **and** the frontend types are
  regenerated and committed.
- A new error `code` was introduced → it is documented and in the OpenAPI document.

If nothing changed, nothing to do. Stale documentation is worse than none, because people
believe it.

## 5. Deployed to staging and smoke-tested

Merged is not deployed, and deployed is not working. The change is on staging, and someone has
actually exercised the changed path there — the real journey, in a browser or against the real
API, not a health check.

Note what was smoke-tested and what the result was.

## 6. No open critical or major findings

Nothing critical or major is outstanding from any source: the quality gate, the AI review, the
human review, security scanning, or the staging smoke test.

An open critical finding with a follow-up ticket is an open critical finding.

---

## Checklist

Copy this into the pull request description.

```markdown
## Definition of Done

- [ ] 1. Every acceptance criterion met, with test output pasted below
- [ ] 2. All CI gates green; quality gate passed (0 new Blocker/Critical/Major, ≥80% new-code coverage)
- [ ] 3. `/review` findings resolved AND a human has approved
- [ ] 4. CLAUDE.md / ADR / OpenAPI + generated types updated, or confirmed unchanged
- [ ] 5. Deployed to staging and smoke-tested — what was tested: ________
- [ ] 6. No open critical or major findings

Spec: docs/specs/____
Evidence:
```

---

## What this is not

It is not a suggestion, a stretch goal, or a target to hit on average. It is the bar for merging
a single change.

It is also not a substitute for judgement. Meeting all six conditions does not make a bad design
good. Review still has to ask whether this was the right thing to build.
