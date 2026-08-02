# Standard: Testing

Testing is required, not optional. **Verification precedes any claim of "done"** and any push.
"It should work" is not a result; a command's output is.

## Backend test projects

| Project | Scope |
|---|---|
| `AjBoilerplate.UnitTests` | Domain rules and Application handlers in isolation — no database, no I/O. Fast. |
| `AjBoilerplate.IntegrationTests` | Application + Infrastructure against a **real SQL Server** with migrations applied. |
| `AjBoilerplate.ArchitectureTests` | Enforces the Clean Architecture dependency rules. Fails the build if a layer imports the wrong direction. |

- Frameworks: xUnit + FluentAssertions; NSubstitute for test doubles.
- Integration tests run against real SQL Server — never an in-memory substitute for behaviour
  that is provider-specific (concurrency tokens, collation, decimal precision, transactions).
- Architecture tests encode [`clean-architecture.md`](clean-architecture.md): Domain depends on
  nothing; Application → Domain; Infrastructure → Application + Domain; Contracts carries no
  business logic; Api is the composition root.

## Frontend tests

- **Vitest** for services, signals, pipes, and component logic.
- **Playwright** (`apps/web-e2e`) for user journeys — at least one per route.
- **axe-core** accessibility assertions on every new or changed screen.
- Type-checking is part of the test surface.

## What must be covered

- **Every domain invariant** in the spec has a test that fails when the invariant is broken.
- **Every authorization rule** has a test proving a disallowed caller cannot reach the
  resource — and, where a field is restricted, that the field is **absent from the response
  payload**, not merely hidden.
- **Every error path** that has a distinct status code or `code` value.
- **Every state transition**, including the illegal ones that must be rejected.
- Concurrency: a test proving a stale `If-Match` / `rowversion` yields `409`.

## How to write them

- **Test behaviour and invariants, not implementation details.** A test that breaks when you
  rename a private method is a maintenance tax, not a safety net.
- Follow test-driven development where it fits: failing test → minimal code → green →
  refactor.
- One reason to fail per test. Arrange/Act/Assert, named for the behaviour under test.
- No test depends on another test's leftovers, on wall-clock time (use `IClock`), or on
  execution order.
- Coverage target: **≥80% on new code**, enforced by the quality gate. Coverage is a floor for
  spotting untested paths, never the goal itself.

## Commands

```bash
dotnet test                                       # all backend test projects
npx nx run-many -t test typecheck                 # frontend unit tests + types
npx nx e2e web-e2e                                # Playwright journeys
```

## Gate

Tests must pass **before** the SonarQube scan and **before** a push is proposed. See
[`sonarqube.md`](sonarqube.md) and [`../commands/pre-push.md`](../commands/pre-push.md).
