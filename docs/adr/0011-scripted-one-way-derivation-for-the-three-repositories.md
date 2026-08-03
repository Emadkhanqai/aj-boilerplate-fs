# ADR-0011: Derivation of the single-stack repositories is a committed one-way script

**Status:** Accepted
**Date:** 2026-08-03
**Deciders:** Boilerplate maintainers
**Supersedes:** —

> Builds on [ADR-0006](0006-three-repository-split.md), which decided to publish three
> repositories and named "a derivation script" and "a drift check" as follow-on work.
> ADR-0006 is not superseded and its decision is unchanged. This ADR fills the gap it
> left open.

---

## Context

ADR-0006 split the boilerplate into three published repositories — `aj-boilerplate-fs`
(the source of truth), `aj-boilerplate-be` (`src/backend/` promoted to the repository
root), and `aj-boilerplate-fe` (`src/frontend/` promoted to the root). It listed drift as
the standing risk of that decision, in as many words: *"Three repositories must be kept in
sync, and there is no mechanism enforcing it. Drift is the standing risk of this decision
and it will happen if propagation is not deliberate."*

It has happened. The ADR numbering has already diverged between `be` and `fe`: the same
decision does not carry the same number in the two repositories, so a cross-reference
written in one is wrong in the other, and neither matches this tree. That is a small
symptom and a precise one — ADR numbers are the most mechanical, least ambiguous thing in
the repository, and they still drifted. Anything requiring judgement has drifted further.

The cause is not carelessness. It is that propagation is currently a manual, undocumented,
multi-step operation performed occasionally by whoever remembers, and there is no artefact
anywhere in this repository that even states what the correct output is. Two people
propagating "the same" change produce two different results, and neither can be checked
against anything, because the rules exist only as prose in ADR-0006 and as habit.

Four forces shape what the fix can be:

- **The derived repositories are outputs, not workspaces.** ADR-0006 is explicit that work
  originates in `fs`. Any mechanism that makes `be` or `fe` a plausible place to make a
  change reintroduces the problem it is solving.
- **There is no shared git history.** ADR-0006 dropped it deliberately, because the source
  project's history contains business content that cannot be published. So there is no
  merge base and no possibility of a git-native sync. Whatever we build copies files.
- **Not everything can be derived mechanically.** `README.md` describes two stacks.
  `CLAUDE.md` exists at the root and again inside each stack, and ADR-0006 says they
  merge. A workflow that names both `src/backend` and `src/frontend` in a build matrix
  needs an entry deleted, not a path rewritten. A script that pretends otherwise produces
  a repository that looks derived and is subtly wrong, which is worse than one that
  obviously needs work.
- **The operation is destructive by shape.** It writes hundreds of files into a directory
  assembled from arguments, and the obvious implementation clears that directory first.
  This is the exact shape of the operation that deletes somebody's work when a variable is
  empty. Whatever we build has to be built as if it will one day be run with the wrong
  argument, because it will be.

## Decision

We will commit a derivation script, `scripts/derive.sh`, and make it the only supported
way to produce `aj-boilerplate-be` and `aj-boilerplate-fe`. Derivation is one-way and
regenerative: the derived trees are rebuilt from `fs`, never edited and merged back.

The script:

- **Enumerates files from git, not from the filesystem.** `git ls-files` is the input, so
  build output, `node_modules`, and `.env` files cannot leak into a published repository —
  not because they are excluded, but because they were never in the list. Tracked-only by
  default; `--include-untracked` exists for iterating locally and warns when it is not set
  and untracked files exist.
- **Promotes and drops by explicit rule.** `src/<stack>/*` moves to the root; the other
  stack's tree, the other stack's CI workflow, its Sonar settings file, and the full-stack
  `docker-compose.yml` are dropped. Everything else — `.claude/`, `.github/` minus the
  other CI, `.editorconfig`, `.gitattributes`, `.gitignore`, `docs/`, `infra/`, the
  governance files — is shared verbatim, which is what ADR-0006 requires.
- **Never silently overwrites on a collision.** A promoted `README.md` landing on the
  shared root `README.md` keeps both, the promoted one suffixed (`README.be.md`), and
  reports the collision. Dotfiles are suffixed correctly: `.editorconfig` becomes
  `.editorconfig.be`, not `.be.editorconfig`, which would be a different file that no tool
  reads.
- **Rewrites in-file path references heuristically, and says so.** `src/backend/x` becomes
  `x` in Markdown, YAML, JSON, and shell files. Files where that substitution produces
  something plausible and wrong — `README.md`, `.github/workflows/supply-chain.yml`,
  `.github/dependabot.yml` — are copied untouched and listed under NEEDS REVIEW.
- **Prints the manual steps it did not do.** Merging `CLAUDE.md`, rewriting `README.md`,
  and pruning the other stack's entries from the matrix-shaped configuration files are
  named as human work, every run.
- **Runs a cross-contamination check** after writing: no `src/frontend` in `be`, no
  `src/backend` in `fe`, no stray `package.json` or `*.csproj` from the other stack, and
  every shared harness file present. This is the drift check ADR-0006 asked for, applied
  at the moment of derivation rather than after publication.

**Dry run is the default.** `scripts/derive.sh` prints the plan and writes nothing.
Writing requires `--write`.

**Nothing is deleted without `--clean`, and `--clean` is guarded four ways.** The target
must be an absolute path, must be under the resolved output base, must be named
`aj-boilerplate-be` or `aj-boilerplate-fe`, and must contain the `.derived-by-derive-sh`
marker file the script itself wrote. Anything else is refused with an explanation. A
directory is not safe to delete because it looks like the one you meant.

**The script does not create git repositories, does not commit, and does not push.** It
produces two directories. What happens to them is a human decision, taken with the diff in
front of them.

Enforcement: the contamination check runs as part of every `--write`, and `--check` runs
it alone against an existing derivation. Release is defined as running `derive.sh`, doing
the manual steps it lists, and reviewing the full diff against each published repository —
described in `CONTRIBUTING.md` and `CHANGELOG.md`.

## Consequences

### Positive

- Propagation is reproducible. Two people running the script on the same commit get the
  same output, and disagreement about what "in sync" means becomes a disagreement about a
  file, which is a resolvable kind.
- The rules of ADR-0006 stop being prose. `COMMON_DROP`, `BE_DROP`, `FE_DROP`, and
  `REVIEW_REQUIRED` in the script are the derivation rules, executable and diffable. A
  change to what is shared is now a reviewable change to a file.
- The ADR-numbering drift that prompted this cannot recur through the derivation path:
  `docs/adr/` is shared verbatim and the check fails if it is missing.
- Secrets and build output cannot leak into a published repository by accident, because
  the file list comes from git.
- The script is genuinely safe to run wrong. That is worth as much as the feature.

### Negative

- **Derivation is still not automatic, and this ADR does not make it so.** Somebody has to
  run the script and do the manual steps. If nobody does, the repositories drift exactly
  as before — the script removes the excuse, not the requirement.
- **The path-rewriting is a heuristic and will be wrong occasionally.** It is a textual
  substitution. It cannot know that `'src/backend/**'` in a workflow path filter should
  become `'**'` rather than `'./**'`. The NEEDS REVIEW list contains the files where we
  know this bites; it will not contain the next one until someone hits it.
- **Three of the most-read files still need a human every time**: `README.md`,
  `CLAUDE.md`, and the matrix-shaped workflow configuration. These are the files a new
  consumer reads first, so the least-automated part of derivation is the most visible one.
- **The derived trees have no history.** Each release is a wholesale replacement, so
  `git log` in `be` and `fe` describes the derivations, not the changes. Bisecting a
  problem there means coming back to `fs`.
- **There is no post-publication drift detector.** The check runs against the freshly
  derived tree, not against what is actually on GitHub. Somebody committing directly to
  `aj-boilerplate-be` is invisible to this, and branch protection on those repositories is
  the only thing that would prevent it.

### Neutral

- A new top-level `scripts/` directory, and a new marker file `.derived-by-derive-sh` in
  each derived tree. The marker is load-bearing — it is what `--clean` checks — and must
  not be tidied away.
- The default output is `dist/derive/`, already covered by `.gitignore`.
- The script is bash and depends only on git, `sed`, and coreutils. It runs on a
  maintainer's machine and in CI without a toolchain.

### Follow-on work

- A scheduled workflow in `fs` that derives and opens a pull request against `be` and `fe`,
  turning "somebody has to remember" into "somebody has to review". Deliberately not built
  now: it needs cross-repository write credentials, which is a security decision that
  should be taken on its own rather than as a footnote to this one.
- A post-publication drift check that fetches the two published repositories and diffs the
  shared file set against `fs`. This is the half of ADR-0006's verification that the
  contamination check does not cover.
- Branch protection on `be` and `fe` forbidding direct commits, so the one-way rule is
  enforced by the platform rather than by convention.
- Reconciling the ADR numbering that has already diverged. The script prevents future
  divergence; it does not repair the existing one, and that is a manual reconciliation
  someone has to sit down and do.

## Alternatives considered

### Do nothing and keep propagating by hand

The status quo. Rejected by evidence rather than by argument: ADR-0006 predicted drift,
and the numbering divergence between `be` and `fe` is that prediction coming true on the
single most mechanical artefact in the repository. Manual propagation does not fail
because people are careless; it fails because the correct output is not written down
anywhere, so there is nothing to be careful against.

### Git subtree or submodule for the shared parts

Real sharing with real history, and it would make `.claude/` genuinely one thing rather
than three copies. Rejected for the reason ADR-0006 gave — submodules confuse newcomers and
a boilerplate's value is that cloning it is trivial — and for a second reason specific to
this problem: the shared set is not one directory. It is `.claude/`, most of `.github/`,
most of `docs/`, and six root files. That is four or five subtrees, each with its own
update dance, in a repository whose whole promise is that you can clone it and start.

### A template repository with generation-time options

ADR-0006 rejected this as disproportionate and that remains true, but it is worth
restating what it would buy: derivation would become a supported feature of the platform
rather than a script, and consumers would get a "regenerate" path too. It loses because
it requires building and maintaining a generator, which is a bigger project than the
boilerplate it generates, and because GitHub template repositories cannot express "promote
this subtree to the root and delete that one".

### A CI workflow that derives and force-pushes to `be` and `fe`

The most complete answer, and the one to build next. Rejected for now on two grounds.
First, it needs credentials with write access to two other repositories, held in this one —
a real expansion of blast radius that deserves its own decision, not a line in this ADR.
Second, the derivation is not fully mechanical yet: three files need a human on every run,
so an automatic push would publish a repository whose `README.md` describes a stack it does
not contain. Automate the push after the manual steps are gone, not before.

### Making the script write directly over an existing clone of `be` / `fe`

Convenient, and it would let `git diff` in that clone show the effect of a derivation
directly. Rejected: it would mean the script's destructive path pointing at a directory
containing somebody's real repository, including their `.git`. The output-directory-plus-
marker-file design exists precisely so that the script can never be aimed at something it
did not create. Copying the derived tree over a clone is a two-line manual step that keeps
that boundary intact.

### One repository, with the consumer deleting the stack they do not want

Rejected in ADR-0006 and not reopened here. Recorded only because it is the alternative
that makes this ADR unnecessary, and it remains available: abandoning the three-repository
split would delete this entire class of problem. That trade is ADR-0006's to revisit.

## Verification

How we know it is being honoured:

- `scripts/derive.sh --check` passes against the current derivation: no cross-stack
  contamination, and every shared harness file present.
- A `--clean` invocation aimed at a directory the script did not create is refused. This
  is worth testing by hand once, because it is the safety property that matters most and
  the one nobody will notice is broken.
- Spot check after a release: the `.claude/` directory and `docs/adr/` are byte-identical
  across all three repositories.
- The ADR index in `docs/adr/README.md` lists the same numbers in all three repositories.
  That is the specific drift that prompted this ADR, so it is the specific thing to check.

How we know it has stopped serving us — any of these should trigger a superseding ADR:

- The NEEDS REVIEW list grows past a handful of files. At that point derivation is mostly
  manual again and the script is providing false confidence.
- Somebody lands a change in `be` or `fe` first. The one-way rule is the assumption every
  part of this design rests on; if it stops holding, the design is wrong rather than the
  person.
- A release goes out where the derived repositories were not regenerated. Once that has
  happened twice, "somebody runs the script" has been shown not to work and the automated
  push becomes necessary rather than optional.

## References

- [ADR-0006](0006-three-repository-split.md) — the three-repository split this builds on
- [scripts/derive.sh](../../scripts/derive.sh) — the mechanism
- [docs/upgrading.md](../upgrading.md) — the related but distinct problem of a *consumer*
  pulling improvements back in, which derivation does not address
- [CONTRIBUTING.md](../../CONTRIBUTING.md) — where derivation sits in the release process
- [CHANGELOG.md](../../CHANGELOG.md) — the release and tagging convention
