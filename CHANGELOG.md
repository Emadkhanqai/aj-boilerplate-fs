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

## [0.1.0] — 2026-08-03

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
- EF Core 10 against SQL Server, migration-based, with two migrations in the box so the
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
