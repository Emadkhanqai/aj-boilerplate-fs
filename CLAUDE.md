# CLAUDE.md — Agentic Full-Stack Boilerplate

Project context for Claude Code. Read this first, then the nested `CLAUDE.md` for whichever
stack you are touching (`src/backend/CLAUDE.md`, `src/frontend/CLAUDE.md`).

> **HARD RULE — secrets.** Never put a secret, password, API key, token, connection string,
> certificate, or any credential in this file, in a prompt, in a commit message, in an ADR, in
> a spec, or in any other context file. Ever. Configuration values that *look* like secrets go
> in the secrets provider (Secret Manager / Key Vault) and are referenced by name only. If you
> find one committed, treat it as an incident: rotate it, then remove it.

---

## What this is

A runnable starting point for a new product: a .NET layered Clean Architecture API, an
Angular + Nx + PrimeNG web app, an OpenAPI contract that generates the frontend's types, a
quality gate, IaC for two cloud providers, and a committed `.claude/` harness.

It contains **no business domain**. The single sample entity, `Item`, exists to prove the whole
path end to end and is designed to be deleted on day one.

## Making it yours (do this first)

1. **.NET identity** — rename `AjBoilerplate` everywhere: the five project folders under
   `src/backend/src/`, the three under `src/backend/tests/`, `AjBoilerplate.slnx`, every
   `namespace`/`using`, `RootNamespace`/`AssemblyName` in the `.csproj` files, and the
   `sonar.projectKey`.
2. **Nx identity** — rename the `@aj-boilerplate` scope in `tsconfig.base.json` paths, every
   library `package.json`/`project.json`, and all import statements. Rename the `web` app if
   you want a different name.
3. **Sample entity** — delete or rename `Item` end to end: domain entity, handlers, DTOs, EF
   configuration + the `InitialCreate` migration, controller, OpenAPI paths, generated types,
   and the `feature-items` library. Delete its tests with it; keep the architecture tests.
4. **Docs** — replace `README.md`, fill in `src/frontend/DESIGN.md` before any UI work, and
   start your own ADR series (keep ours as `0001`–`0006` history or delete them).
5. **Infra** — pick your provider, set project/subscription variables, configure a remote
   state backend, and delete the tree you are not using.

## Stack

| Layer | Technology |
|---|---|
| API | .NET 10, ASP.NET Core, EF Core 10 |
| Database | Microsoft SQL Server (migration-based, never `EnsureCreated`) |
| Cache | Redis (Memorystore on GCP, Cache for Redis on Azure — protocol-identical) |
| Web | Angular 21, Nx, PrimeNG, TypeScript strict |
| AuthN | Google Cloud Identity (`gcp`) or Microsoft Entra ID (`azure`) |
| AuthZ | Keycloak — provider-independent, roles and policies live there |
| Tests | xUnit + Testcontainers-based integration suite; Vitest; Playwright |
| Quality | SonarQube Community Build (free, self-hosted), Gitleaks, CodeQL, `dotnet format`, ESLint, Prettier |

## Architecture

```
src/backend/
├── AjBoilerplate.slnx
├── src/
│   ├── AjBoilerplate.Domain/          entities, domain exceptions — zero dependencies
│   ├── AjBoilerplate.Application/     handlers, ports (abstractions), validation
│   ├── AjBoilerplate.Contracts/       DTOs, ApiResponse<T>, PagedResponse<T>
│   ├── AjBoilerplate.Infrastructure/  EF Core, repositories, cache, secrets, messaging
│   └── AjBoilerplate.Api/             controllers, middleware, DI, auth, observability
└── tests/
    ├── AjBoilerplate.UnitTests/
    ├── AjBoilerplate.IntegrationTests/
    └── AjBoilerplate.ArchitectureTests/   ← enforces the rule below, in CI

src/frontend/
├── apps/web, apps/web-e2e
└── libs/
    ├── auth/                  guard, interceptor, token handling
    ├── data-access/api-client, data-access/api-types   ← api-types is GENERATED
    ├── shared/ui, shared/util
    ├── shell/                 layout, sidebar, top bar, nav
    └── feature-items/         the sample feature
```

**The layer dependency rule.** Dependencies point inward, one direction only:

```
Api → Infrastructure → Application → Domain
Api → Contracts
```

- `Domain` references nothing. No EF Core, no ASP.NET, no third-party framework.
- `Application` may reference `Domain` — and **not** `Contracts`. The Application layer owns its
  own models; `Api` maps them to the wire DTOs. Otherwise a breaking API change would force a
  change to the use cases themselves. It declares ports (interfaces); it never references
  `Infrastructure` or `Api`.
- `Contracts` references nothing else. It is the wire format, shared with API consumers.
- `Infrastructure` implements those ports. It is the only project that knows about EF Core,
  Redis, HTTP clients, or a cloud SDK.
- `Api` composes. Controllers are thin: validate, delegate to a handler, shape a response.
- Business rules live in `Domain` and `Application`. Never in a controller, never in a
  repository, never in the frontend.

`AjBoilerplate.ArchitectureTests` fails the build if any of this is violated. Do not weaken it.

## Commands

Run from the repository root unless noted.

### Backend

```bash
dotnet restore  src/backend/AjBoilerplate.slnx
dotnet build    src/backend/AjBoilerplate.slnx -warnaserror
dotnet format   src/backend/AjBoilerplate.slnx --verify-no-changes   # CI-equivalent check
dotnet test     src/backend/tests/AjBoilerplate.UnitTests
dotnet test     src/backend/tests/AjBoilerplate.ArchitectureTests
dotnet test     src/backend/tests/AjBoilerplate.IntegrationTests     # needs SQL Server + Redis
dotnet run    --project src/backend/src/AjBoilerplate.Api            # http://localhost:5080
dotnet list     src/backend/AjBoilerplate.slnx package --vulnerable --include-transitive
```

### Migrations

```bash
dotnet ef migrations add <Name> \
  --project        src/backend/src/AjBoilerplate.Infrastructure \
  --startup-project src/backend/src/AjBoilerplate.Api
dotnet ef database update \
  --project        src/backend/src/AjBoilerplate.Infrastructure \
  --startup-project src/backend/src/AjBoilerplate.Api
```

Prefer the `/new-migration` command — it reviews the generated SQL before anything is applied.

### Frontend

```bash
cd src/frontend
npm ci
npx nx serve web                 # http://localhost:4200
npx nx lint  web
npx nx test  web --coverage
npx nx build web --configuration=production
npx nx run web-e2e:e2e           # Playwright
npx nx affected -t lint,test,build
npm run generate:api             # regenerate api-types from the running API's OpenAPI doc
npm audit --audit-level=high
```

### Whole-repo gate

`/qa` runs the full local gate (build, format, lint, tests, SonarQube). `/pre-push` runs it and
reports readiness. Neither pushes.

## The `CLOUD_PROVIDER` switch

`CLOUD_PROVIDER` (env var, bound to `Cloud:Provider` in configuration) accepts `gcp` or
`azure`. Set it before running the API; there is no default that silently guesses.

| Concern | `gcp` | `azure` |
|---|---|---|
| Secrets | Secret Manager | Key Vault + Managed Identity |
| Identity (authN) | Google Cloud Identity | Microsoft Entra ID |
| Authorization | Keycloak | Keycloak |
| Cache | Memorystore | Cache for Redis |
| IaC | `infra/gcp/` (Terraform) | `infra/azure/` (Bicep) |

Only secrets and identity branch in code, behind `ISecretsProvider` and the authentication
setup in `AjBoilerplate.Api`. Everything else is provider-agnostic. If you add a third
provider, add it in those two places — not scattered through feature code.

## Conventions

**API envelope.** Every response, success or failure, is `ApiResponse<T>`:
`{ success, data, message, errors, statusCode, code, timestamp, traceId }`. Collections use
`PagedResponse<T>`. A result filter applies the envelope; controllers return the payload.

**Status codes.** `200` read, `201` created (with `Location`), `204` delete, `400` validation,
`401` unauthenticated, `403` unauthorised, `404` missing, `409` conflict or concurrency,
`422` domain-rule violation, `429` rate-limited, `500` unhandled. Never `200` with
`success: false`.

**Error codes.** `code` is a stable `SCREAMING_SNAKE_CASE` string the frontend may branch on
(`ITEM_NOT_FOUND`, `VALIDATION_FAILED`, `CONCURRENCY_CONFLICT`). `message` is human-readable
and may change; `code` may not. Exceptions map to codes in the `IExceptionHandler` chain —
never construct an error response by hand in a controller.

**Routing.** `/api/v{version}/{resource}` — plural, kebab-case, versioned from day one.

**Migrations.** Every schema change is an EF Core migration, reviewed as SQL before it is
applied. Never edit a migration that has been applied anywhere but your own machine; add a new
one. Never `EnsureCreated`, never out-of-band DDL.

**Generated API types.** `libs/data-access/api-types` is generated from the API's OpenAPI
document by `npm run generate:api`. Never hand-edit it, and never hand-write a DTO that
duplicates a server type — change the contract, regenerate, then use it. `/sync` does this.

**Frontend.** Standalone components, signals first, `inject()` over constructor injection,
`OnPush` everywhere, strict TypeScript, no `any`. PrimeNG is the only component library —
no native `<input>`, `<select>`, `<button>`, or `<table>` in feature code. Dropdowns are
filterable and sorted A–Z by default.

**Tests.** Failing test first, then the code. Unit tests for domain and handler logic,
integration tests for anything crossing a boundary, Playwright for critical journeys.

## Non-negotiable rules

1. **No secrets in context.** See the rule at the top of this file.
2. **Never `git push` without explicit human approval**, on any branch, to any remote, every
   time. Committing is fine; pushing is a human decision.
3. **The quality gate runs before any push is proposed.** Zero new Blocker, Critical, or Major
   SonarQube findings; ≥80% coverage on new code. Minor and Info may be triaged. The gate
   targets **SonarQube Community Build** (free, self-hosted): one project, one branch, no
   branch analysis and no pull-request decoration — never pass `sonar.branch.name`,
   `sonar.pullrequest.*`, or a `branch`/`pullRequest` MCP argument. See
   `.claude/standards/sonarqube.md`.
4. **Build with warnings as errors.** A warning is a failure.
5. **Respect the layer dependency rule.** If a change needs to break it, the design is wrong.
6. **Migration-based schema changes only.**
7. **PrimeNG only** in the frontend; **generated types only** for API contracts.
8. **One task per session, fresh context per task.** No unattended multi-hour runs.
9. **Human review is mandatory** and is never waived because an agent wrote the code. The
   developer who prompted it owns it. Keep PRs to roughly 400 changed lines.
10. **Update the docs with the change.** If a convention changed, `CLAUDE.md` changes in the
    same PR. If a decision was made, an ADR lands with it. If a contract changed, the OpenAPI
    document and the generated types change with it.
11. **Classify the task and state the model tier before the first tool call.** Frontier tier
    for architecture, security review, complex debugging, high-risk refactors, and the final
    pre-push review; workhorse tier for everything else. Say the recommendation out loud in
    the first reply — and if this session is on a costlier model than the work needs, **stop
    and say so** rather than spending it. The `model-routing` hook injects this on every
    prompt; the policy is `.claude/model-routing.md`.

## Where to look next

| Topic | Path |
|---|---|
| Deeper standards (one file per topic) | `.claude/standards/` |
| Layering rules in detail | `.claude/standards/clean-architecture.md` |
| Slash commands | `.claude/commands/` |
| Hooks and their triggers | `.claude/hooks/` · `.claude/README.md` |
| Model routing (enforced every prompt) | `.claude/model-routing.md` |
| The SonarQube gate, Community Build setup | `.claude/standards/sonarqube.md` |
| Five-stage workflow and guardrails | [docs/workflow.md](docs/workflow.md) |
| Definition of Done | [docs/definition-of-done.md](docs/definition-of-done.md) |
| Day-1 checklist | [docs/onboarding.md](docs/onboarding.md) |
| Spec template | [docs/specs/TEMPLATE.md](docs/specs/TEMPLATE.md) |
| Architecture decisions | [docs/adr/](docs/adr/) |
| API contract workflow | [docs/api/README.md](docs/api/README.md) |
| Session handoffs | [docs/handoff/](docs/handoff/) |
| Infrastructure | [infra/gcp/README.md](infra/gcp/README.md) · [infra/azure/README.md](infra/azure/README.md) |
