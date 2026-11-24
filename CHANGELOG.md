# Changelog

Notable changes to this boilerplate. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) as adapted below.

**This file is written for the person upgrading, not for the person who made the change.**
A changelog entry that reads like a commit message has failed at its only job. Every entry
should answer: what changed, whether I have to do anything about it, and what happens if I
ignore it. If an entry needs a migration step, the step is in the entry — not in a linked
issue, not in a pull request comment.

For how to actually pull these changes into a project that cloned this boilerplate, see
[docs/upgrading.md](docs/upgrading.md).

---

## Versioning, for a boilerplate

Semantic Versioning is defined in terms of an API. A boilerplate has no API — it has a
shape that consumers have already copied. So the meanings are pinned explicitly:

| Bump | Means | Examples |
|---|---|---|
| **MAJOR** | A consumer who cloned an earlier version has to change their own code to take this. | A layer moves or is renamed · the `ApiResponse<T>` envelope changes shape · a hook's contract or opt-out variable changes · a configuration key is renamed or removed · a `.claude/` standard reverses a rule people wrote code against. |
| **MINOR** | Something new that an existing consumer can take or leave. | A new hook · a new workflow · a new ADR · a new optional configuration key with a safe default · a new document. |
| **PATCH** | A fix or a clarification with no shape change. | A hook bug · a broken CI step · a wrong path in a document · a dependency bump with no behaviour change. |

Two consequences worth stating, because both surprise people:

- **A documentation change can be MAJOR.** If a standard in `.claude/standards/` reverses a
  rule, consumers who followed it are now non-compliant. That is a breaking change even
  though no code moved.
- **A dependency bump is usually PATCH here, even when the dependency's own bump is
  MAJOR.** What matters is the effect on a consumer's tree, and a consumer who cloned this
  boilerplate owns their own `package.json`.

## Releasing

1. Everything intended for the release is merged to `main` and the gate is green.
2. Move the `[Unreleased]` entries into a new version section with today's date. Rewrite
   them for the upgrader if they still read like commit subjects.
3. Choose the bump using the table above. The Conventional Commits types in the range are
   an input, not the answer: `feat` suggests minor and `!`/`BREAKING CHANGE` suggests
   major, but a `docs:` commit that reverses a standard is still major.
4. Tag it. **Annotated tags only** — a lightweight tag carries no author, date, or message,
   and the tag is the thing consumers pin to.

   ```bash
   git tag -a v1.2.0 -m "v1.2.0 — container image scanning, SBOM, Dependabot"
   git push origin v1.2.0
   ```

5. Regenerate the single-stack repositories: `scripts/derive.sh --write --clean`, do the
   manual steps it prints, review the diff, push, and tag those repositories with the same
   version. **The three repositories share a version number.** They are one artefact
   published in three shapes (ADR-0006), and a consumer comparing `aj-boilerplate-be`
   v1.2.0 against `aj-boilerplate-fs` v1.2.0 must be comparing the same content.
6. Create the GitHub release from the tag, with this file's section as the body.

Tag format is `vMAJOR.MINOR.PATCH`. Pre-releases, if ever needed, are `v1.2.0-rc.1` and are
never derived to the single-stack repositories — those track releases only.

---

## [Unreleased]

Nothing yet.

---

## [1.0.0] — 2026-11-24

**A major release, because sign-in was replaced and configuration keys changed.** If you cloned
0.1.x and kept its authentication, you cannot take this release by merging; read
[Upgrade steps from 0.1.x](#upgrade-steps-from-01x) first. If you have not touched the
authentication code, the steps are mostly configuration.

The short version: **the browser no longer holds a token.** Sign-in is now a server-side flow run
by a new Auth service, nginx in the web container is the gateway, and the API only ever sees
Keycloak JWTs. The reasoning is in [ADR-0012](docs/adr/0012-gateway-session-auth.md) and the model
is in [docs/auth/](docs/auth/README.md).

### Breaking changes

- **Browser-side OIDC is gone.** The SPA no longer signs in against an identity provider, holds a
  token, refreshes one, or attaches `Authorization`. `libs/auth` has two providers now:
  `gateway` (sign-in is a navigation to `/auth/login`) and `dev` (the role picker). The OIDC
  provider, its callback page and every stored token are deleted. *If you ignore this:* any code you
  wrote against the old provider, token seams (`AUTH_TOKEN_PROVIDER`, `TOKEN_REFRESHER`) or
  `/auth/callback` route no longer compiles or never runs.
- **Web container runtime configuration changed.** `AUTH_MODE` is now `gateway` (default) or `dev`;
  it was `dev` (default) or `oidc`. **`AUTH_MODE=oidc` now stops the container at start-up.**
  `OIDC_CLIENT_ID`, `OIDC_AUTHORITY` and `OIDC_SCOPE` are removed. New: `AUTH_PROVIDER`
  (`embedded` | `usp`), `AUTH_UPSTREAM`, and `API_UPSTREAM` is now an environment variable instead
  of a hard-coded `proxy_pass`. `src/frontend/nginx.conf` is replaced by three templates in
  `src/frontend/docker/nginx/`, chosen at start.
- **A container that was serving the app now answers `401`/`302` until a session exists.** With the
  default `AUTH_PROVIDER=embedded`, every request to `/` and `/api/` is gated by the Auth service.
  Run `AUTH_MODE=dev` for the old ungated role-picker behaviour, local use only.
- **API: direct cloud tokens are no longer accepted.** The `Authentication:GoogleCloudIdentity:*`
  and `Authentication:EntraId:*` schemes are removed, together with `Keycloak:Realm` and
  `Keycloak:ClientSecret`, which nothing read. The API now validates only Keycloak JWTs whose `azp`
  equals `Keycloak:ClientId`. *If you ignore this:* a bearer token from Google or Entra is rejected
  with 401, and the removed keys are silently ignored.
- **API: new start-up refusals outside Development.** A plaintext `Keycloak:JwksUri` stops the API
  unless you set the new `Keycloak:AllowInsecureJwksTransport=true`; an empty `JwksUri` stops it
  unless `Keycloak:AllowDiscoveryFallback=true`; an `Auth:Mode` other than `gateway` stops it.
- **API: deny by default.** A fallback policy now requires an authenticated caller on every
  endpoint that declares nothing. The three health endpoints opt out with `AllowAnonymous`. If you
  added an endpoint outside a controller, it now answers 401 until you decide its policy.
- **API: policies are "a seat OR a permission".** `ReadAccess`, `WriteAccess` and `AdminAccess` are
  satisfied by the role hierarchy or by `items:read`, `items:write` or `admin:manage`. A role key
  the application does not recognise is dropped and logged once, never granted.
- **API: the rate limiter runs after authentication** and partitions on the user's subject. Before,
  every caller behind the gateway shared one bucket keyed by the proxy's address.
- **`CLOUD_PROVIDER` no longer selects an identity issuer.** It selects the secrets provider and the
  `infra/` tree. [ADR-0002](docs/adr/0002-dual-cloud-provider-behind-one-switch.md)'s identity half
  is superseded by ADR-0012 (a note sits at its top; its body is unchanged).
- **New database objects.** The app database gets an `AuthenticationEvents` table (migration
  `AddAuthenticationEvents`), and the Auth service needs its own database with schema `auth`, applied
  by its own migration. Neither runs at start-up.
- **Root `docker-compose.yml` has new services** (`keycloak`, `auth-migrate`, `auth`) and the API now
  waits for Keycloak. The old `OIDC_*` and `Authentication__*` entries are gone from `.env.example`.
- **Generated API types moved.** `npm run generate:api` now reads `docs/api/openapi.json` and writes
  `libs/data-access/api-types/src/lib/generated.ts`. The hand-written `types.ts` stays for the
  sample `Item` shapes, because the generated ones are all optional.

### Added

- **`AjBoilerplate.Auth`**, a separate ASP.NET Core host: Authorization Code + PKCE sign-in against
  Entra ID, RFC 8693 token exchange at Keycloak, encrypted sessions in SQL Server with Redis in front,
  8 h idle and 12 h absolute lifetimes, single-flight refresh, `/auth/validate`, a roles-version
  endpoint, a revoked marker, and an admin force-refresh endpoint. `AjBoilerplate.Auth.Tests` and an
  architecture test that keeps it independent of the layered API.
- **`AUTH_PROVIDER=embedded|usp`.** `embedded` runs your own Auth service. `usp` is for projects
  inside the Al Jazeera USP platform: the shared gateway and auth-backend do the job and **you do not
  deploy the Auth service**.
- **`AUTH_MODE=gateway|dev`** and the nginx gateway: `auth_request`, JWT injection, 404 on
  `/auth/validate` and `/internal/`, security headers with a CSP nonce.
- **`GET /api/v1/me`**, returning roles, capability flags and `permissions`. (0.1.x documented it as
  something you would add.)
- **API identity layer**: JWKS pre-warm, seats and permissions (`ApplicationRoles`, `Permissions`,
  `RoleCapabilities`, `SeatOrPermission`), and `SignInRecordingMiddleware`, which records one row per
  gateway session.
- **SPA behaviour**: gateway auto sign-in with a five-minute loop brake, a role-change watcher
  (polls every 15 s while the tab is visible), an access-revoked dialog, and an "access check
  unavailable" page.
- **A local sign-in stack with no Microsoft tenant**: the bundled Keycloak realm
  (`infra/keycloak/realm-aj-boilerplate.json`, dev-only secrets) and seeded users `admin`, `editor`,
  `viewer` and `noaccess`.
- **Documentation**: [`docs/auth/`](docs/auth/README.md) (overview, setup, troubleshooting, contract)
  and ADR-0012. The root `README.md` was rewritten around a five-minute quickstart.

### Changed

- Auth service defaults equal the Al Jazeera USP staging Entra values (scopes
  `openid profile offline_access User.Read`, `RequestOwnClientIdScope=false`, `SubjectIssuer=oidc`).
  Azure AD B2C stays available by configuration.
- **Deriving the single-stack repositories.** `scripts/derive.sh` now emits one `.env.example` per
  stack from the root file, re-points Markdown links per repository, drops the other stack's `.claude/`
  agents and standards (and, for the frontend, `infra/` and the backend deploy workflows), and does
  not ship itself. `generate:api` and the `UserProfile` contract spec find `docs/api/openapi.json`
  from either layout. The ADR numbers of this repository are the canonical ones in all three. See
  [ADR-0011](docs/adr/0011-scripted-one-way-derivation-for-the-three-repositories.md).
- A user holding more than one seat now sees the highest in the top bar, sidebar and home page
  (it showed whichever the token listed first).
- A patched `System.Security.Cryptography.Pkcs` is pinned. `Microsoft.Data.SqlClient` 6.1.1 pulls a
  version that now carries two advisories, which fail restore under warnings-as-errors.

### Removed

- The browser OIDC client, its stored tokens, the `/auth/callback` SPA route and the `OIDC_*`
  variables.
- The API's `Authentication:GoogleCloudIdentity` and `Authentication:EntraId` schemes, and
  `Keycloak:Realm` and `Keycloak:ClientSecret`.
- `src/frontend/nginx.conf`.

### Not in this release

- **Nothing deploys the Auth service, the web container or Keycloak.** `infra/` and `deploy.yml`
  still provision and roll out the API, its database, Redis and secrets only. Projects using
  `embedded` deploy the rest themselves.
- **No real Entra tenant has been exercised in this repository.** The local stack replaces Entra with
  a second Keycloak client. See [docs/auth/setup.md](docs/auth/setup.md).

### Upgrade steps from 0.1.x

1. **Decide your provider.** Outside USP: `AUTH_PROVIDER=embedded`, and you will deploy the Auth
   service. Inside the Al Jazeera USP platform: `AUTH_PROVIDER=usp`, and ask the USP platform team to
   onboard your host and client (see [setup, section 3](docs/auth/setup.md#3-onboarding-into-usp-auth_providerusp)).
2. **Web container.** Replace `AUTH_MODE=oidc` with `gateway`; delete `OIDC_*`; set `AUTH_PROVIDER`,
   `API_UPSTREAM` and (for `embedded`) `AUTH_UPSTREAM`. Take `src/frontend/docker/` and the new
   `Dockerfile` wholesale, and remove `nginx.conf`.
3. **API configuration.** Delete `Authentication__*`, `Keycloak__Realm` and `Keycloak__ClientSecret`.
   Set `Keycloak__ValidIssuer`, `Keycloak__JwksUri` (https) and `Keycloak__ClientId`. If your
   Keycloak serves its JWKS over http, decide deliberately about
   `Keycloak__AllowInsecureJwksTransport`.
4. **Keycloak.** The portal client needs token exchange, client roles `admin`/`editor`/`viewer`
   (plus any permissions) and a flat `roles` claim mapper. See
   [setup, section 2b](docs/auth/setup.md#2b-keycloak).
5. **Migrations.** Apply `AddAuthenticationEvents` to the app database. For `embedded`, create the
   Auth database and apply the Auth service's migration (`docker compose` does this with
   `auth-migrate`).
6. **Frontend.** If you customised `libs/auth`, re-apply your changes onto the `gateway` provider;
   the token seams are gone. Run `npm run generate:api` and commit the result.
7. **Compose and CI.** Take the new root `docker-compose.yml`. Add the Auth service image build and
   deploy to your pipeline if you use `embedded`; the boilerplate's `deploy.yml` does not.
8. **ADRs.** Continue your own series at the next free number after `0012`.
9. Run the gate. Then sign in as a user with a role, as one without, and remove a role in Keycloak to
   watch the access-revoked dialog appear.

---

## [0.1.1] — 2026-09-01

A patch release, and every entry in it is the same story: a tree that was green in August
went red in September without anybody touching it. Two advisories were published against
packages nobody referenced directly, and one instruction in the upgrade guide went stale
because the boilerplate grew. Nothing changes shape — but if you kept the sample
integration fixture, there is a two-line change to make.

### Fixed

- `docs/upgrading.md` told you to start your own ADR series at `0008`, and called the shipped
  set "the seven ADRs". There are eleven, so `0008`–`0011` are taken and that advice collided
  with real files. It now says to count `docs/adr/` at the version you cloned, because this
  number moves with every release. **If you already numbered your own ADRs from `0008`, you
  have a clash to reconcile** — `docs/adr/README.md` is explicit that numbers are never
  reused or renumbered, so renumber *yours*, not the boilerplate's.

- `Testcontainers.MsSql` 4.4.0 → 4.14.0. 4.4.0 pulls `SSH.NET` 2024.2.0 transitively, which
  now carries a high-severity advisory (GHSA-q939-rpr3-3284, patched in 2026.0.0). Because
  `TreatWarningsAsErrors` is on, this fails `dotnet restore` with `error NU1903` — the build
  goes red on a package nobody referenced directly, on a machine that never changed. 4.14.0
  resolves `SSH.NET` 2026.0.0. **Nothing to do but take it**, unless you had pinned
  Testcontainers yourself. One source change came with it: `MsSqlBuilder()`'s parameterless
  constructor is obsolete in 4.14.0, so the image moves into the constructor
  (`new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")`) and the `.WithImage(...)`
  call is dropped. If you kept the sample fixture, apply the same two-line change.

- **`npm audit` went red on eleven high advisories, all in build tooling.** The Nx toolchain
  moves to 23.1.3 (a patch, not a major), and `less` is pinned past 4.6.7 by an override —
  which also resolves the `image-size` chain underneath it. Nothing that ships in the browser
  bundle was affected; these are build-time dependencies, and the gate audits them on purpose,
  because a compromised build tool is a compromised artefact. `package-lock.json` was
  regenerated rather than patched, so expect a large lockfile diff and take it wholesale.
  Lint, unit tests, typecheck and the production build all pass on the new tree.

- **The frontend image failed the container scan.** The `nginx:alpine` base is rebuilt on
  nginx's schedule rather than Alpine's, so between those rebuilds it ships packages for
  which Alpine has already published a fix — here `libcrypto3` and `libssl3`
  (CVE-2026-14456) and `libexpat` (CVE-2026-66046), four HIGH findings, all fixable. The
  runtime stage now runs `apk upgrade --no-cache`, which closes the window without pinning
  a base tag that goes stale and without an allowlist entry — `.trivyignore.yaml` ships
  empty on purpose. **Take this one**: if you built from the v0.1.0 Dockerfile, the same
  gap is in your image.

---

## [0.1.0] — 2026-08-04

The initial extraction, published as three repositories. Everything below is the starting
state rather than a change from something; subsequent entries will read as changes.

### Added

**Backend — .NET 10**

- Layered Clean Architecture: `Domain` → `Application` → `Contracts` → `Infrastructure` →
  `Api`, with an architecture test suite that fails the build when the dependency direction
  is violated ([ADR-0001](docs/adr/0001-layered-clean-architecture.md)).
- A uniform `ApiResponse<T>` envelope with a stable machine-readable `code`, a `traceId`,
  and a documented status-code contract. Exceptions map to responses through an ordered
  handler chain rather than through try/catch in controllers
  ([ADR-0005](docs/adr/0005-apiresponse-envelope-and-status-code-contract.md)).
- EF Core 10 against SQL Server, migration-based, with three migrations in the box so the
  workflow is demonstrated rather than described.
- Transactional outbox and inbox, a health-check split into liveness and readiness,
  fixed-window rate limiting, security headers, forwarded-headers handling, opaque entity
  identifiers, Serilog with URL sanitisation, and OpenTelemetry traces and metrics.
- Two clouds behind one switch: `CLOUD_PROVIDER=gcp|azure` selects the secrets provider and
  the identity issuer at the composition root
  ([ADR-0002](docs/adr/0002-dual-cloud-provider-behind-one-switch.md)).

**Frontend — Angular 21 + Nx**

- Standalone components, signals, `OnPush`, strict TypeScript, and library boundaries
  enforced by Nx tags.
- PrimeNG as the only component library
  ([ADR-0003](docs/adr/0003-primeng-as-sole-component-library.md)), with one bounded
  exception recorded in [ADR-0007](docs/adr/0007-bespoke-whats-new-modal.md).
- API types generated from the OpenAPI document rather than hand-written
  ([ADR-0004](docs/adr/0004-openapi-generated-frontend-types.md)).
- An offline demo configuration that swaps in MSW, so the app runs with no backend.

**The "What's new" feature spotlight**

- A popup that shows each user a newly shipped feature exactly once, the first time they
  land on a URL prefix it is bound to. Acknowledgement is server-side per user, so it
  survives cleared browser storage and other devices. Shipping the next announcement is an
  INSERT-only migration and no code at all. See [docs/whats-new.md](docs/whats-new.md).

**Infrastructure**

- Terraform for Google Cloud (Cloud Run, Cloud SQL, Memorystore, Secret Manager) and Bicep
  for Azure (Container Apps, Azure SQL, Cache for Redis, Key Vault), provisioning the same
  logical shape.
- Deployment through dev → staging → prod with GitHub Environment protection rules as the
  approval gates, authenticating by OIDC with no long-lived cloud credentials anywhere.

**The agentic harness**

- `.claude/` ships committed, not gitignored: hooks, slash commands, standards, agents, and
  templates, so every developer and every CI run gets identical guardrails.

**Quality gate**

- Build with warnings as errors, format verification, ESLint, unit + integration +
  architecture tests, Playwright, SonarQube Community Build (zero new
  Blocker/Critical/Major, ≥80% coverage on new code), Gitleaks, CodeQL, and dependency
  vulnerability scanning.

**Process documentation**

- A five-stage workflow, a Definition of Done, a spec template, a Day-1 onboarding
  checklist, and an architecture guide that matches the code.

**Repository governance and toolchain** *(this release)*

- `LICENSE` — an explicit all-rights-reserved notice recording that the licence choice is
  pending. It grants and removes nothing; it replaces a guess with a statement.
- `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, `CODEOWNERS`, a pull-request
  template that embeds the Definition of Done checklist, and issue forms.
- **Conventional Commits**, enforced by `.claude/hooks/commit-msg.sh` in both git-hook and
  agent-hook mode, with a `COMMIT_MSG_SKIP=1` opt-out.
- `.gitattributes` — normalises line endings so a Windows clone does not fail
  `dotnet format` and does not break every shell hook with `bad interpreter`.
- `.env.example` — every environment variable the repository reads, what it is for, and
  whether it is required.
- A **root `docker-compose.yml`** bringing up database, API, and web together, with
  migrations applied as a discrete step before the API starts.
- `.vscode/extensions.json` and `settings.json`, matching `.editorconfig` and `.prettierrc`
  rather than competing with them.
- SonarQube configuration as files rather than inline CI arguments, so a local scan and a
  CI scan analyse the same code under the same rules.
- `.github/dependabot.yml` covering NuGet, npm, GitHub Actions, and Docker base images,
  grouped and weekly so it does not produce pull-request spam.
- `.github/workflows/supply-chain.yml` — builds both container images, scans them with
  Trivy, fails on fixable HIGH/CRITICAL, and publishes CycloneDX and SPDX SBOMs. Runs
  weekly as well as on change, because an image goes stale without a commit. The allowlist
  (`.trivyignore.yaml`) requires a justification and an expiry date, and CI fails when an
  entry outlives its date.
- **Database migrations wired into deployment.** `deploy.yml` applies a migration bundle
  built by CI from the same commit, before the rollout, with the expand → migrate →
  contract rule documented in full.
- `docs/incidents/` with a template and guidance, `docs/upgrading.md` for consumers pulling
  improvements back in, and `scripts/derive.sh` — the committed derivation script recorded
  in [ADR-0011](docs/adr/0011-scripted-one-way-derivation-for-the-three-repositories.md).

### Known limitations

Stated here rather than discovered later:

- **No licence is set.** Until `LICENSE` is replaced with a real one, no reuse rights are
  granted and outside contributions cannot be accepted.
- **Placeholders must be filled before this repository is useful to anyone else**: the
  CODEOWNERS handles, the `<owner>/<repo>` URLs in `SECURITY.md` and the issue-template
  config, the Code of Conduct enforcement contact, and the SonarQube project keys.
- **No pull-request decoration from SonarQube.** Community Build analyses one branch;
  findings appear on the dashboard and through the local pre-push hook, never as PR
  comments.
- **Derivation is not automatic.** `scripts/derive.sh` regenerates the single-stack
  repositories but three files still need a human on every run, and nothing checks the
  published repositories for drift after the fact.

[Unreleased]: https://github.com/<your-org>/aj-boilerplate-fs/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/<your-org>/aj-boilerplate-fs/compare/v0.1.1...v1.0.0
[0.1.1]: https://github.com/<your-org>/aj-boilerplate-fs/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/<your-org>/aj-boilerplate-fs/releases/tag/v0.1.0

### Also in this first tag

Fixes made between writing the entries above and cutting the tag. They are listed because
they are real defects that a consumer would otherwise inherit, not because anything here
changes shape.

- **Dependency advisories cleared.** Angular moves to 21.2.19, which patches an
  HttpTransferCache cache-key ambiguity and two XSS advisories in packages that ship to the
  browser. Nx moves to 23.1.1, and `brace-expansion`, `postcss` and `undici` are pinned
  forward through `overrides`. `npm audit --audit-level=high` reports nothing, so the gate
  passes on its merits rather than by having its threshold moved.
- **Coverage was never produced.** The CI command passed `--coverageReporters`, which is
  valid on the Angular executor and invalid on `@nx/vite`, so it crashed exactly the two
  projects using the latter while the other six wrote reporters that do not include lcov.
  The reporter now lives in each project's own configuration. `sonar.javascript.lcov.reportPaths`
  pointed at a single file that has never existed and is now a wildcard.
- **The page scrolled sideways on a phone.** The topbar is a flex row whose items cannot
  shrink below their content, so it grew past the viewport and took the document with it —
  43px of overflow at 320px. It now wraps and the title truncates. The items table, wider
  than a phone by nature, gained the `.scroll` container `DESIGN.md` already required.
- **The migration bundle and the container scan could not run.** The bundle left the compile
  to `dotnet ef`, which reports only "Build failed"; it is an explicit Release build now.
  The Trivy action was pinned to a tag that does not exist.
- Two `vitest.config.mts` files and one Playwright spec used `__dirname` in an ES module.
