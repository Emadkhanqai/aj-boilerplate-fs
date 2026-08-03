# Contributing

This repository ships its own guardrails, so contributing to it is mostly a matter of
letting them run. Read [docs/workflow.md](docs/workflow.md) for how a change moves through
the five stages, and [docs/definition-of-done.md](docs/definition-of-done.md) for what
"done" means here. This page is the mechanics: how to set up, what to run, what will block
you, and why.

A note on scope before anything else. This is a **domain-free boilerplate**. The most
common rejected contribution is a good feature that belongs in a product rather than here.
If a change only makes sense once you know what the application does, it belongs in your
clone.

---

## Setting up

**Prerequisites:** .NET SDK 10, Node.js 22+, Docker, and git. That is the whole list.

```bash
git clone https://github.com/<your-org>/aj-boilerplate-fs.git
cd aj-boilerplate-fs

cp .env.example .env          # then set MSSQL_SA_PASSWORD; nothing else is required
docker compose up --build     # database + API + web, one command
```

That brings up the whole stack: the web app on <http://localhost:4200>, the API on
<http://localhost:8080> with its OpenAPI UI at `/swagger`, and SQL Server with the
migrations already applied. `.env.example` documents every environment variable the
repository reads and what each is for.

Working on one stack only? The per-stack instructions are in
[src/backend/README.md](src/backend/README.md) and
[src/frontend/README.md](src/frontend/README.md), and each has its own compose file for
running just its dependencies.

### Install the git hooks

Two of the hooks in `.claude/hooks/` also work as git hooks and are not installed
automatically — a repository that installs hooks on clone is a repository that runs code
you have not read.

```bash
ln -s ../../.claude/hooks/secret-scan.sh .git/hooks/pre-commit
ln -s ../../.claude/hooks/commit-msg.sh  .git/hooks/commit-msg
```

The first blocks a commit containing something that looks like a credential. The second
enforces the commit convention below. Both are also wired as Claude Code hooks in
`.claude/settings.json`, so an agent-made commit is checked with or without the symlinks;
these two lines are what covers a commit you make by hand.

---

## Branch and pull-request flow

`main` is protected and always releasable. Everything else is a short-lived branch off it.

```bash
git switch -c feat/short-description        # or fix/…, docs/…, chore/…
```

Branch names are not enforced by anything. The prefix matching the Conventional Commits
type is a convention because it makes `git branch --list` readable, not because a hook
cares.

1. **Spec first for anything non-trivial.** `docs/specs/TEMPLATE.md`, approved by a human
   before implementation starts. A bug fix does not need one; a feature does.
2. **Failing test first.** A test that has never been observed failing has not been shown
   to test anything — that sentence is in the Definition of Done and it is meant literally.
3. **Keep the diff small.** A pull request that changes the harness and the application in
   one commit is one that cannot be cherry-picked by a consumer later, which matters more
   in a boilerplate than in a product. See [docs/upgrading.md](docs/upgrading.md).
4. **Open the pull request.** The template fills in automatically and embeds the Definition
   of Done checklist. It is not decoration: a reviewer will ask for the evidence it asks
   for.
5. **Green gate, then human review.** In that order, and both. An agent-written change gets
   more scrutiny, not less — it is fluent and confident, which makes a wrong approach look
   like a right one. Whoever prompted it owns every line and must be able to explain it.
6. **Squash on merge.** One commit per pull request on `main`, with a Conventional Commit
   subject, because the changelog is assembled from that history.

---

## The quality gate

Everything below must pass before a pull request can merge. None of it is advisory.

| Gate | Backend | Frontend |
|---|---|---|
| Build | `dotnet build -warnaserror` | `nx build web --configuration=production` (AOT is the real typecheck) |
| Format | `dotnet format --verify-no-changes` | Prettier + ESLint |
| Unit tests | `AjBoilerplate.UnitTests` | `nx affected -t test --coverage` |
| Architecture tests | `AjBoilerplate.ArchitectureTests` — layer dependency direction | Nx tag boundaries via ESLint |
| Integration tests | `AjBoilerplate.IntegrationTests` against a real SQL Server (Testcontainers) | — |
| End to end | — | Playwright |
| Static analysis | SonarQube, CodeQL | SonarQube, CodeQL |
| Dependencies | `dotnet list package --vulnerable` | `npm audit --audit-level=high` |
| Secrets | Gitleaks | Gitleaks |
| Images | Trivy + SBOM (`.github/workflows/supply-chain.yml`) | Trivy + SBOM |

The SonarQube conditions specifically: **zero new Blocker, Critical, or Major findings**
and **at least 80% coverage on new code**. Minor and Info may be triaged, with the triage
recorded.

Run it locally before you push:

```bash
# backend
dotnet build src/backend/AjBoilerplate.slnx -warnaserror
dotnet format src/backend/AjBoilerplate.slnx --verify-no-changes
dotnet test  src/backend/AjBoilerplate.slnx

# frontend
cd src/frontend && npx nx affected -t lint,test,build
```

Or `/pre-push`, which runs the lot and reports readiness without pushing.

### Two things about SonarQube that will otherwise confuse you

This targets **SonarQube Community Build**, which analyses exactly one branch: the
default one. Consequently the scanner runs only on pushes to `main`, and **there is no
pull-request decoration** — Sonar findings never appear as PR comments. Branch analysis
and PR decoration start at Developer Edition and this boilerplate deliberately does not
depend on them.

What covers the gap is `.claude/hooks/sonar-pre-push.sh`, which blocks a push while any
Blocker, Critical, or Major is open on the project. It **fails closed**: with SonarQube
unreachable or unconfigured, the gate has not passed, so the push is blocked. Configure
`SONAR_HOST_URL` and `SONAR_TOKEN` (see `.env.example`), or use `SONAR_GATE_SKIP=1` — which
is a tech-lead decision and must be recorded in the pull request, not a way around a
Tuesday afternoon.

Analysis settings live in `SonarQube.Analysis.xml` (backend) and
`sonar-project-frontend.properties` (frontend), and both the hook and CI read the same
files. That is what makes a local scan and a CI scan agree.

---

## Commit convention

**[Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/)**, enforced
by `.claude/hooks/commit-msg.sh`. It blocks rather than warns, because the commit history
is an input to the release process — the changelog is assembled from it and the version
bump is derived from the types present. A history that is 90% conventional is worse than
none: it looks parseable, so somebody writes a parser, and the parser is wrong about the
rest.

```
<type>[optional scope][!]: <subject>

[optional body]

[optional footer(s)]
```

| Type | Use it for | Release effect |
|---|---|---|
| `feat` | A new capability | minor |
| `fix` | A defect corrected | patch |
| `docs` | Documentation only | none |
| `style` | Formatting with no behaviour change | none |
| `refactor` | Neither fixes a bug nor adds a feature | none |
| `perf` | A performance improvement | patch |
| `test` | Tests only | none |
| `build` | Build system or dependencies | none |
| `ci` | CI configuration and workflows | none |
| `chore` | Everything else with no source effect | none |
| `revert` | Reverts a previous commit | context-dependent |

**Scope** is optional and free-form; use the part of the system that changed — `api`,
`web`, `infra`, `harness`, `deps`.

**Breaking changes** are marked with `!` after the type or scope, or with a
`BREAKING CHANGE:` footer, and drive a major version bump. In a boilerplate a breaking
change is anything a consumer who cloned last month would have to react to: a renamed
layer, a changed API envelope, a removed hook, a moved configuration file.

**Rules the hook checks:** a known type, a colon and exactly one space, a non-empty
subject, no trailing full stop, a header of 100 characters or fewer, and a blank line
before any body. It also rejects subjects that say nothing (`fix: bug`, `chore: stuff`).
Merge, revert, and fixup messages that git generates are left alone.

```
feat(api): return a stable error code on validation failure
fix(web): stop the items grid refetching on every keystroke
refactor(infrastructure)!: rename ISecretsProvider.GetAsync to FetchAsync
docs: record the three-repository sync mechanism in ADR-0011
ci: scan container images and publish an SBOM
```

Genuinely stuck with a message you do not control — an automated merge, a tooling-generated
revert? `COMMIT_MSG_SKIP=1 git commit …`. It is not for "I will tidy it up later"; in
practice the history is append-only and you will not.

---

## How the `.claude/` harness fits in

`.claude/` ships **committed, not gitignored**, so every developer and every CI run gets
identical guardrails. The `.gitignore` says so at the top, in capitals, with the single
exception of `.claude/settings.local.json`. Do not add anything under `.claude/` to
`.gitignore`.

The distinction that matters is between prose and enforcement:

- **`.claude/standards/`, `commands/`, `agents/`, `templates/` are prose.** An agent reads
  them, usually follows them, and occasionally does not.
- **`.claude/hooks/` and `.claude/settings.json` are deterministic.** They fire every time,
  identically, and a `PreToolUse` hook can refuse a tool call before it happens.

Which is why a rule worth enforcing goes in a hook, not only in a document.

| Hook | Fires on | What it does |
|---|---|---|
| `model-routing.sh` | prompt submit | Classifies the task and recommends a model tier |
| `block-dangerous.sh` | before Bash | Refuses destructive commands outright |
| `commit-msg.sh` | before Bash, and as a git hook | Enforces Conventional Commits |
| `sonar-pre-push.sh` | before Bash | Blocks a push while the quality gate is failing |
| `protect-files.sh` | before Edit/Write | Refuses edits to protected paths |
| `auto-format.sh` | after Edit/Write | Formats what was just written |
| `secret-scan.sh` | after Edit/Write, and as a pre-commit hook | Blocks credentials reaching a file |
| `run-affected-tests.sh` | after Edit/Write | Runs the tests the change touched |

### Contributing to the harness

Changes to `.claude/` are the highest-leverage and highest-risk contributions here: they
change how every future change is made, in three repositories.

- **A new hook must fail safe and be testable from the command line.** Every existing hook
  reads its input from stdin or from `$1` and can be exercised without an agent. Copy that
  idiom — `secret-scan.sh` and `commit-msg.sh` are the two dual-mode examples.
- **Exit 2 blocks; exit 0 allows.** Anything else is a bug in the hook.
- **Every hook that blocks needs a documented opt-out** with a name matching the existing
  ones (`SONAR_GATE_SKIP`, `COMMIT_MSG_SKIP`), and the opt-out must be loud when used. A
  gate with no escape hatch gets disabled wholesale the first time it is wrong.
- **Document it in `.env.example` section 10 and in the table above** in the same pull
  request.
- **Never edit `.claude/settings.json`'s `extraKnownMarketplaces` or `enabledPlugins`**
  as a side effect of another change.

---

## Architecture decisions

Write an ADR when a decision is expensive to reverse, crosses team or layer boundaries,
constrains future work, or will provoke "why on earth is it like this?" from someone who
was not there. Do not write one for a choice a single pull request can undo.

Copy `docs/adr/TEMPLATE.md` to `docs/adr/NNNN-short-slug.md` using the next free number,
and update the index in [docs/adr/README.md](docs/adr/README.md) in the same pull request.
Never edit an accepted ADR to reflect a new decision — write a new one that supersedes it.

An ADR with no negative consequences has not been thought about.

---

## Incidents

If something broke in a deployed environment, write it up:
[docs/incidents/README.md](docs/incidents/README.md) says when, and
`docs/incidents/TEMPLATE.md` says how. It is blameless and it is a search index for future
pain, not a compliance artefact.

---

## Releasing, and the three repositories

This boilerplate is published as three repositories — `aj-boilerplate-fs` (this one, the
source of truth), `aj-boilerplate-be`, and `aj-boilerplate-fe`. **All work originates
here.** The single-stack repositories are outputs, regenerated by `scripts/derive.sh`;
never make a change in one of them, because the next derivation will overwrite it.

```bash
scripts/derive.sh                    # dry run — prints the plan, writes nothing
scripts/derive.sh --write --clean    # produce both trees under dist/derive/
```

It prints the manual steps it cannot do. Do them, review the whole diff against each
published repository, then push deliberately.

The release and tagging convention is in [CHANGELOG.md](CHANGELOG.md). The background is
[ADR-0006](docs/adr/0006-three-repository-split.md) and
[ADR-0011](docs/adr/0011-scripted-one-way-derivation-for-the-three-repositories.md).

---

## Reporting problems

- **A bug or a feature idea** — open an issue; the forms ask for what a maintainer will
  need anyway.
- **A security vulnerability** — do not open an issue. [SECURITY.md](SECURITY.md) explains
  the private reporting path.
- **Behaviour in the community** — [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Note that its
  enforcement contact is an unfilled placeholder until the repository owner sets one.

## Licensing of contributions

There is no licence on this repository yet — see [LICENSE](LICENSE), which records that
the position is all-rights-reserved and that the choice is pending a decision that is not
an engineer's to make. **Until that is resolved, outside contributions cannot be accepted**,
because there are no terms to accept them under. This is not a judgement about the
contribution; it is that nobody here is in a position to grant or receive rights. If you
have something to contribute, open an issue describing it and it can be revisited once the
licence is settled.
