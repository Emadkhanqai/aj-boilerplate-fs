<div align="center">

# Agentic Full-Stack Boilerplate

**A .NET 10 + Angular 21 starting point that ships with its own engineering guardrails.**

[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Angular](https://img.shields.io/badge/Angular-21-DD0031?logo=angular&logoColor=white)](https://angular.dev/)
[![Nx](https://img.shields.io/badge/Nx-monorepo-143055?logo=nx&logoColor=white)](https://nx.dev/)
[![PrimeNG](https://img.shields.io/badge/UI-PrimeNG-41B883)](https://primeng.org/)
[![SQL Server](https://img.shields.io/badge/SQL_Server-EF_Core_10-CC2927?logo=microsoftsqlserver&logoColor=white)](https://learn.microsoft.com/ef/core/)
[![Licence](https://img.shields.io/badge/licence-not%20set-lightgrey)](#licence)

</div>

---

## What this is

Most "starter templates" give you a folder structure and leave the hard parts — layering that
actually holds, an API contract the frontend can trust, a quality gate that blocks bad merges,
infrastructure you can read — as an exercise. This one ships those parts working, with a single
sample entity proving the whole path end to end.

It is also built to be driven by [Claude Code](https://claude.com/claude-code). A `.claude/`
harness ships **committed, not gitignored**: hooks that format on save, block dangerous shell
commands, protect sensitive files, run the affected tests, scan for secrets, and gate a push on
the quality gate — plus slash commands for the recurring work.

It contains **no business domain**. The one sample entity, `Item`, is designed to be deleted on
your first day.

## Why it exists

Every new service re-litigates the same decisions: where do business rules live, what shape is
an error response, who owns the API contract, what blocks a merge, how do we get to two clouds
without forking the codebase. Those decisions are made here, written down as
[ADRs](docs/adr/), and enforced by tests and CI rather than by convention.

## What you get

- **Layered Clean Architecture backend** — `Domain` → `Application` → `Contracts` →
  `Infrastructure` → `Api`, with an architecture test suite that fails the build when the
  dependency direction is violated.
- **A consistent API envelope** — every response is `ApiResponse<T>` with a stable
  machine-readable `code`, a `traceId`, and a documented status-code contract. Exceptions map
  to responses through a handler chain, not through try/catch in controllers.
- **OpenAPI as the contract** — the frontend's TypeScript types are *generated* from the API's
  OpenAPI document. Hand-written DTOs on the client are a bug. See [docs/api/](docs/api/).
- **Angular 21 + Nx + PrimeNG** — standalone components, signals, `OnPush`, strict TypeScript,
  library boundaries enforced by Nx tags, PrimeNG as the only component library.
- **EF Core migration workflow** — MSSQL, migration-based, with exactly one `InitialCreate` in
  the box so the workflow is demonstrated rather than described.
- **Two clouds, one switch** — `CLOUD_PROVIDER=gcp|azure` selects the secrets provider and the
  identity issuer at the composition root, and selects which `infra/` tree deploys. Terraform
  for GCP, Bicep for Azure, provisioning the same logical shape.
- **A real quality gate** — build with warnings as errors, `dotnet format` verification, ESLint,
  unit + integration + architecture tests, Playwright E2E, SonarQube (zero new
  Blocker/Critical/Major, ≥80% coverage on new code), Gitleaks, CodeQL, dependency
  vulnerability scanning.
- **The agentic harness** — 7 hooks and the `/spec`, `/task`, `/qa`, `/review`, `/implement`,
  `/pre-push`, `/quality-gate`, `/new-migration`, `/sync` commands, all committed.
- **A written process** — a [five-stage workflow](docs/workflow.md), a
  [Definition of Done](docs/definition-of-done.md), a [spec template](docs/specs/TEMPLATE.md),
  and a [Day-1 onboarding checklist](docs/onboarding.md).

What it is **not**: a platform, a CMS, an auth server, or a deployment you can apply as-is.
`infra/` ships as reviewed IaC with no state and no real project identifiers — you configure a
state backend and your own values before the first `apply`.

## Quickstart (about 5 minutes)

**Prerequisites:** .NET SDK 10, Node.js 22+, a reachable SQL Server and Redis, and a Keycloak
realm if you want authorization enforced locally.

```bash
# 1 — clone
git clone https://github.com/<your-org>/aj-boilerplate-fs.git
cd aj-boilerplate-fs

# 2 — configure. Export what the API needs, or put it in a local .env — either way
#     nothing here is committed; .gitignore already excludes .env and .env.*
export CLOUD_PROVIDER=gcp          # or: azure
export ConnectionStrings__Default='Server=localhost,1433;Database=AjBoilerplate;User Id=sa;Password=<your-local-password>;TrustServerCertificate=True;'
export ConnectionStrings__Redis='localhost:6379'

# 3 — database. Apply the single InitialCreate migration.
dotnet tool install --global dotnet-ef
dotnet ef database update \
  --project        src/backend/src/AjBoilerplate.Infrastructure \
  --startup-project src/backend/src/AjBoilerplate.Api

# 4 — run the API  →  http://localhost:5080  (OpenAPI UI at /swagger)
dotnet run --project src/backend/src/AjBoilerplate.Api
```

In a second terminal:

```bash
# 5 — run the web app  →  http://localhost:4200
cd src/frontend
npm ci
npx nx serve web
```

Open <http://localhost:4200> and go to **Items** — create, edit, and delete a row. That round
trip exercises the Angular feature library, the generated API types, the controller, the
handler, the repository, and the migration you just applied. (Routes are guarded, so you will
be asked to sign in first once you have pointed the app at your identity provider and Keycloak
realm.)

Then verify the gate is green before you change anything:

```bash
dotnet build src/backend/AjBoilerplate.slnx -warnaserror
dotnet test  src/backend/tests/AjBoilerplate.UnitTests
cd src/frontend && npx nx affected -t lint,test,build
```

Full command reference: [CLAUDE.md](CLAUDE.md).

## Repository map

```
.
├── CLAUDE.md              project context for Claude Code — read this first
├── .claude/               committed agentic harness: hooks, commands, standards, agents
├── .mcp.json              MCP server configuration
├── src/
│   ├── backend/           .NET 10 solution (5 source projects, 3 test projects)
│   └── frontend/          Nx workspace (apps/web, apps/web-e2e, libs/*)
├── docs/
│   ├── adr/               architecture decision records (+ template)
│   ├── specs/             feature specs (+ template)
│   ├── api/               how the OpenAPI contract is produced and consumed
│   ├── handoff/           session handoffs written by the Stop hook
│   ├── onboarding.md      Day-1 checklist
│   ├── workflow.md        Spec → Plan → Execute → Verify → Review
│   └── definition-of-done.md
├── .github/
│   ├── workflows/         backend-ci · frontend-ci · deploy (+ its reusable per-environment job)
│   └── gitleaks.toml      secret-scanning config; extends the default ruleset
└── infra/
    ├── gcp/               Terraform: Cloud Run, Cloud SQL, Memorystore, Secret Manager
    └── azure/             Bicep: Container Apps, Azure SQL, Cache for Redis, Key Vault
```

## Related repositories

The same boilerplate is published in three shapes. Pick the one that matches your project — the
single-stack repos are derived from this one, not forks that drift.

| Repository | Contents |
|---|---|
| [`aj-boilerplate-fs`](https://github.com/<your-org>/aj-boilerplate-fs) | This repo — backend + frontend + infra |
| [`aj-boilerplate-be`](https://github.com/<your-org>/aj-boilerplate-be) | Backend only, promoted to the repo root |
| [`aj-boilerplate-fe`](https://github.com/<your-org>/aj-boilerplate-fe) | Frontend only, promoted to the repo root |

All three share `.claude/`, `docs/`, `.gitignore`, and `.editorconfig`.

## CI configuration

The workflows in `.github/workflows/` need the following repository settings. They are **not**
included and CI will not pass until you provide them. Cloud authentication uses GitHub OIDC —
there are no long-lived cloud credentials in any workflow, and none should ever be added.

**Repository variables** (*Settings → Secrets and variables → Actions → Variables*)

| Variable | Used by | Purpose |
|---|---|---|
| `CLOUD_PROVIDER` | deploy | `gcp` or `azure` — selects which IaC runs |
| `SONAR_HOST_URL` | backend CI | Your SonarQube server URL. **The quality-gate job skips itself while this is unset** — see the comment in `backend-ci.yml` and remove the guard once you have a server. |
| `SONAR_PROJECT_KEY` | backend CI | The project key on that server |
| `API_IMAGE` | deploy | Container image for the API, tag or digest |
| `NAME_PREFIX` | deploy (gcp) | Short resource-name prefix, 12 characters or fewer |
| `GCP_PROJECT_ID` · `GCP_REGION` | deploy (gcp) | Target project and region |
| `TF_STATE_BUCKET` | deploy (gcp) | Existing GCS bucket holding Terraform state |
| `AZURE_LOCATION` · `AZURE_NAME_PREFIX` | deploy (azure) | Target region and resource-name prefix |
| `AZURE_SQL_ADMIN_OBJECT_ID` · `AZURE_SQL_ADMIN_LOGIN` | deploy (azure) | Entra principal (use a group) that administers Azure SQL |

**Repository secrets**

| Secret | Used by | Purpose |
|---|---|---|
| `SONAR_TOKEN` | backend CI | SonarQube analysis token |
| `GCP_WORKLOAD_IDENTITY_PROVIDER` | deploy (gcp) | Full workload identity provider resource name |
| `GCP_SERVICE_ACCOUNT` | deploy (gcp) | Service account CI impersonates |
| `AZURE_CLIENT_ID` · `AZURE_TENANT_ID` · `AZURE_SUBSCRIPTION_ID` | deploy (azure) | Federated identity credential for OIDC |
| `GITLEAKS_LICENSE` | both CIs | Optional. Only needed for organisation-owned **private** repositories; Gitleaks is free on public ones. |

Both cloud paths bootstrap themselves: the identity CI uses is created by the same IaC, so the
first deployment is run locally and the resulting values become the secrets above. Both
`infra/*/README.md` files walk through it.

**Environments** — create `dev`, `staging`, and `prod` under *Settings → Environments*. Add
required reviewers to `staging` and `prod`. Those protection rules **are** the approval gates in
`deploy.yml`; without them it promotes straight to production unreviewed.

## Contributing

Read [docs/workflow.md](docs/workflow.md) and [docs/definition-of-done.md](docs/definition-of-done.md)
before opening a pull request. In short: spec first, failing test first, keep the diff small,
green gate, and a human reviews every change — including the ones an agent wrote.

## Licence

**No licence is set yet.** Until a `LICENSE` file is added, default copyright applies and no
reuse rights are granted. The repository owner should choose a licence and commit the
corresponding `LICENSE` file, carrying the appropriate copyright line.
