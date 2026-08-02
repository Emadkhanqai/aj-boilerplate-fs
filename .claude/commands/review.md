---
description: Review the current diff against architecture, standards, spec correctness, OWASP, middleware order, and API contracts. Reports findings; does not push.
---

# /review

Review the working changes before proposing a push.

## Do this

1. `git diff` and `git diff --staged` to see the whole change. Review the diff, not your memory
   of what you intended to write.
2. Check it against the spec in `docs/specs/` — does it implement what was agreed, and only
   that?
3. Walk the checklist in [`../workflows/code-review.md`](../workflows/code-review.md) (the
   agent form is [`../agents/code-reviewer.md`](../agents/code-reviewer.md)).
4. Cross-check the relevant [`../standards/`](../standards/) files, specifically:
   - **Access control / OWASP** — deny by default, object ownership checked after load,
     restricted fields removed by DTO projection rather than hidden
     ([`../standards/owasp-security.md`](../standards/owasp-security.md),
     [`../standards/security.md`](../standards/security.md)).
   - **API** — versioned route, `ApiResponse<T>` with `traceId`, the correct status code per
     the table (including hide-as-404, 409-for-concurrency, 410-vs-404), OpenAPI updated, no
     silent contract break
     ([`../standards/api-response-format.md`](../standards/api-response-format.md),
     [`../standards/api-versioning.md`](../standards/api-versioning.md)).
   - **Middleware order** — reviewed on every backend architecture review; the wrong order
     causes security and authorization bugs
     ([`../standards/middleware.md`](../standards/middleware.md)).
   - **Error handling** — no stack trace, SQL, or internal detail leaking; correct status and
     `code` ([`../standards/error-handling.md`](../standards/error-handling.md)).
   - **EF Core / database** — migration present, business-named, hand-reviewed, not an edit to
     an applied one; `decimal` money; append-only audit; `AsNoTracking` and pagination;
     concurrency token ([`../standards/efcore-migrations.md`](../standards/efcore-migrations.md)).
   - **Frontend** — standalone + OnPush + signals + `inject()`, PrimeNG only, typed reactive
     forms, generated API types only, no `any`, all four data states handled, axe-core clean
     ([`../standards/angular.md`](../standards/angular.md),
     [`../standards/typescript.md`](../standards/typescript.md)).
   - **Tests** — every new behaviour, error path, and authorization rule covered
     ([`../standards/testing.md`](../standards/testing.md)).

## Output

Prioritised findings, most severe first, each with `file:line` and marked **blocker** or
**nit**. Do not approve a push while any correctness, security, or architecture blocker is
open. The final Blocker/Critical/Major decision still defers to the SonarQube gate
([`quality-gate.md`](quality-gate.md)).
