---
description: Implement one task from an approved plan, following the standards and TDD. Builds and tests; never pushes.
---

# /implement `<task>`

Implement a scoped piece of work — one task from [`/task`](task.md), or one clearly bounded
change.

## Before writing code

1. **Read the spec** in `docs/specs/` for this feature, and the ADRs in `docs/adr/` that
   constrain it. If the spec is silent on a behaviour you need, **ask** — do not invent a
   business rule.
2. Read the applicable files in [`../standards/`](../standards/), and the workflow that matches
   the change: [`new-feature.md`](../workflows/new-feature.md),
   [`api-change.md`](../workflows/api-change.md), or
   [`database-change.md`](../workflows/database-change.md).
3. If there is a task list, follow it **one task at a time**. Do not start task 3 because task
   2 "was easy".

## While implementing

- **Work on a branch, never `main`.** TDD where it fits: failing test → minimal code → green →
  refactor → commit.
- Respect the Clean Architecture boundaries and the Nx module boundaries.
- **Backend:** DTOs only at the boundary — never bind EF Core entities; FluentValidation on
  every request; `ApiResponse<T>` with `traceId`; versioned routes; deny-by-default
  authorization with an object-ownership check after load; restricted fields removed by DTO
  projection; `decimal` for money; audit append-only.
- **Schema change** → an EF Core migration with a business-intent name, reviewed by hand. Never
  edit an applied migration.
- **Frontend:** standalone components, OnPush, signals, `inject()`, typed reactive forms,
  PrimeNG only, generated API types only. Read `src/frontend/DESIGN.md` before building UI.
- Start from the templates rather than from scratch:
  [`domain-entity.md`](../templates/domain-entity.md),
  [`api-controller.md`](../templates/api-controller.md),
  [`angular-component.md`](../templates/angular-component.md).
- Keep the diff to the task. An unrelated "while I was in here" fix belongs in its own commit.

## Finish

- `dotnet build && dotnet test`; `npx nx run-many -t lint typecheck test build` where the
  frontend changed.
- If the API surface changed: update OpenAPI and run [`/sync`](sync.md).
- Run [`/qa`](qa.md), then [`/review`](review.md).
- **Do not push.** Summarise what you did, what you verified (with the actual command output),
  and what is still open. Then wait for explicit approval.
