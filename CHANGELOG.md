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

[Unreleased]: https://github.com/<your-org>/aj-boilerplate-fs/compare/v0.1.0...HEAD
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
