# Workflow: New Feature

> **Model routing (do first):** classify the task and recommend a model — see
> [`../model-routing.md`](../model-routing.md). New-capability *design* → frontier tier; the
> *build and tests* that follow → workhorse tier. Say so if the current model is mismatched.

End-to-end flow for a new capability across the stack.

## 1. Understand & plan

- **Read the spec** in `docs/specs/`. If there is no approved spec, run
  [`/spec`](../commands/spec.md) first — building without one is how scope drifts.
- Read the ADRs in `docs/adr/` that constrain the area, and the applicable
  [`../standards/`](../standards/).
- List the **invariants** the feature must hold. Each one becomes a test.
- Break the work down with [`/task`](../commands/task.md) if it is more than half a day.

## 2. Branch

`git switch -c feature/<short-desc>`. Never work on `main` directly.

## 3. Backend (if applicable)

1. **Domain** — entities and invariants, persistence-ignorant. Guard the rules inside the
   aggregate so an invalid state is unconstructible.
2. **Application** — commands/queries + handlers, ports, **FluentValidation on every request**
   ([`../standards/input-validation-sanitization.md`](../standards/input-validation-sanitization.md)).
   Enforce policy + scope + **object ownership after loading the resource**
   ([`../standards/owasp-security.md`](../standards/owasp-security.md)).
3. **Infrastructure** — EF Core configuration and repositories; any schema change goes through
   [`database-change.md`](database-change.md).
4. **Contracts** — DTOs and the **`ApiResponse<T>`** envelope. **Never bind EF entities.**
5. **Api** — thin **versioned** controllers (`/api/v1/...`); errors through the central handler
   chain with `traceId`; every status documented in OpenAPI. Respect the middleware order
   ([`../standards/middleware.md`](../standards/middleware.md)).
6. **Tests** — Unit + Integration + Architecture, including a negative authorization test.

## 4. Sync contracts

Update OpenAPI, refresh the snapshot in `docs/api/`, and regenerate the frontend types with
[`/sync`](../commands/sync.md) into `libs/data-access/api-types`. See
[`api-change.md`](api-change.md).

## 5. Frontend (if applicable)

1. **Read `src/frontend/DESIGN.md` before writing any component.**
2. Feature library under `src/frontend/libs/feature-<name>`; all HTTP through
   `data-access/api-client` using **generated** types; **versioned** endpoints only.
3. Standalone components, `OnPush`, signals, `inject()`, **typed reactive forms**, PrimeNG
   only. Handle **loading / error / empty / success**; surface `traceId` on errors.
4. **Role-aware UI is UX, never security.** No `innerHTML`/`bypassSecurityTrust*` with user
   content.
5. Vitest for logic, a Playwright journey for the new route, axe-core clean.

## 6. Verify locally

```bash
cd src/backend  && dotnet build && dotnet test
cd src/frontend && npx nx run-many -t lint typecheck test build
```

## 7. Review & gate

Run [`/qa`](../commands/qa.md), then [`/review`](../commands/review.md), then
[`pre-push-quality-gate.md`](pre-push-quality-gate.md). Fix every Blocker/Critical/Major.
**Do not push without explicit approval.**
