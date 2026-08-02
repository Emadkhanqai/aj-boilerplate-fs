# ADR-0006: Publish as three repositories derived from one source tree

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Boilerplate maintainers

---

## Context

Not every project needs both halves. A team building a service with no web UI should not clone
an Angular workspace, run `npm ci`, and then delete it. A team building a frontend against an
existing API should not inherit a .NET solution, a migration workflow, and SQL infrastructure.
Extra directories are not free: they slow CI, appear in searches, confuse newcomers about what
is in scope, and get half-maintained.

At the same time, the shared parts — the `.claude/` harness, the process documentation, the
ignore rules — must not fork. If the backend-only repository's hooks drift from the full-stack
repository's hooks, the boilerplate has stopped being one thing.

## Decision

We will publish three repositories, all derived from this single full-stack tree:

| Repository | Contents |
|---|---|
| `aj-boilerplate-fs` | The source of truth — backend, frontend, infra, docs, harness |
| `aj-boilerplate-be` | `src/backend/` promoted to the repository root; backend CI only |
| `aj-boilerplate-fe` | `src/frontend/` promoted to the repository root; frontend CI only |

Derivation rules:

- **Shared verbatim** across all three: `.claude/`, `.mcp.json`, `.gitignore`, `.editorconfig`,
  and the stack-neutral pages of `docs/`.
- **`aj-boilerplate-be`** drops `src/frontend/`, `frontend-ci.yml`, and the frontend-specific
  docs; the backend `CLAUDE.md` merges into the root one; `infra/` keeps SQL, Redis, and the API
  runtime.
- **`aj-boilerplate-fe`** drops `src/backend/`, `backend-ci.yml`, and the migration and database
  docs; the frontend `CLAUDE.md` merges into the root one; it keeps a committed OpenAPI document
  so types can be generated without a running API.
- **Documentation is written stack-scoped**, one page per stack rather than one page covering
  both, precisely so derivation is a matter of deleting whole files rather than editing
  paragraphs. This constraint shapes how `docs/` is organised.
- Changes are made in `aj-boilerplate-fs` first and propagated outward. The single-stack repos
  are outputs, not places to originate work.
- No shared git history. Each repository starts fresh; history in the source project is not
  carried over.

## Consequences

### Positive

- Each repository is exactly as large as the project that clones it needs, and its CI runs only
  the gates that apply.
- A backend team never sees a frontend failure, and vice versa. Signal stays high.
- Stack-scoped documentation is better documentation anyway — a page that hedges "on the
  backend… meanwhile on the frontend…" is harder to read even in the full-stack repo.

### Negative

- Three repositories must be kept in sync, and there is no mechanism enforcing it. Drift is the
  standing risk of this decision and it will happen if propagation is not deliberate.
- A change to a shared file is three pull requests, or one scripted propagation that someone has
  to write and maintain.
- Consumers who start single-stack and later need the other half face a merge rather than an
  addition.
- Issues and discussions fragment across three trackers.

### Neutral

- Losing git history is a real loss of context, accepted because the source tree's history
  contains business content that cannot be published — the same reason this boilerplate exists
  as an extraction rather than a fork.
- Derivation must be re-run on every release, so it should be scripted rather than manual.

### Follow-on work

- A derivation script (delete paths, promote a subtree, merge the nested `CLAUDE.md`) run as
  part of releasing a new version, so propagation is mechanical.
- A drift check comparing the shared files across the three repositories.

## Alternatives considered

### One full-stack repository only

Zero drift, one place to change anything. Rejected because it forces every consumer to carry a
stack they may not want, and "just delete the folder you don't need" leaves dangling CI, dead
documentation links, and ignore rules for paths that no longer exist.

### Git submodules or a subtree for the shared parts

Real sharing with real history. Rejected: submodules are a well-known source of confusion for
newcomers, and a boilerplate's whole value proposition is that cloning it is trivial.

### Template repository with generation-time options

The most flexible option, and the right one at a larger scale. Rejected as disproportionate —
it requires building and maintaining a generator, which is a bigger project than the boilerplate
it would generate.

### One repository with three branches

Rejected. Long-lived divergent branches are the worst of both worlds: they drift like separate
repositories while looking like one.

## Verification

Cross-contamination check before each release: no frontend path in `aj-boilerplate-be`, no
backend path in `aj-boilerplate-fe`, and the shared files byte-identical across all three. Every
internal documentation link resolves in whichever repository the page ships in.

## References

- [README.md](../../README.md) — related repositories
- [ADR-0001](0001-layered-clean-architecture.md) — the backend structure that gets promoted
