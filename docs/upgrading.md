# Upgrading from the boilerplate

You cloned this six months ago. You have a real product on top of it now. The boilerplate has
moved on — a hook was fixed, a workflow gained a job, an ADR changed how errors are shaped — and
you want some of that without unpicking your own work.

This page is how. It is also honest about the parts that do not work, because the failure mode
here is not "the upgrade is hard", it is "someone spent two days on a merge that was never going
to converge".

---

## The core problem, stated precisely

**A boilerplate is a starting point, not a dependency.** That sentence is the whole difficulty. A
dependency has a version, an interface, and a resolver. A starting point has none of those: the
moment you clone it, every file becomes yours, and there is no mechanism that can tell your
deliberate change apart from the boilerplate's stale original.

Three specific consequences, and it is worth being clear about which is which:

1. **There is no upstream remote.** `git clone` records `origin` as the boilerplate, but the first
   thing every consumer does is repoint `origin` at their own repository. Nothing remembers where
   the tree came from. `git pull upstream main` does not work because there is no `upstream`.

2. **There is no shared history to merge along.** [ADR-0006](adr/0006-three-repository-split.md)
   drops git history deliberately — the source project's history contains content that cannot be
   published, and the three derived repositories each start fresh. So even after you add the
   remote, the two histories have **no common ancestor**. Git's three-way merge needs a merge
   base; without one it either refuses (`refusing to merge unrelated histories`) or, with
   `--allow-unrelated-histories`, falls back to treating every differing file as a conflict.

3. **Even with a merge base, it would be the wrong one.** Suppose you did clone with history. Your
   merge base would be the commit you cloned at, and *every file you have touched since* would
   diff against the boilerplate's original. That is correct behaviour and useless output: you did
   not fork the boilerplate to track it, you forked it to leave it behind.

So the realistic question is not "how do I merge upstream". It is **"which parts of upstream do I
want, and how do I get exactly those"**.

---

## Decide what you are actually upgrading

Split the tree by how mergeable each part is. This taxonomy is the most useful thing on this page:
if you get the layer right, the strategy follows almost automatically.

| Layer | Paths | Mergeability | Why |
|---|---|---|---|
| **Harness and process** | `.claude/`, `.github/workflows/`, `.mcp.json`, `.editorconfig`, `.gitignore`, `docs/` process pages | **Nearly always mergeable** | You almost certainly have not edited them. They are stack-shaped, not product-shaped, and they are where most of the boilerplate's ongoing value lives. |
| **Infrastructure and packaging** | `infra/gcp/`, `infra/azure/`, `src/*/Dockerfile`, `src/*/docker-compose.yml`, `src/frontend/nginx.conf`, added CI jobs | **Mergeable with care** | You have parameterised these — project IDs, regions, resource prefixes, secret names. Take the structural change, keep your values. Review every line. |
| **Application source** | `src/backend/src/**`, `src/frontend/apps/**`, `src/frontend/libs/**` | **Effectively unmergeable** | This is your product. The boilerplate's version is a demonstration you were meant to grow past. |
| **Schema history** | `src/backend/src/AjBoilerplate.Infrastructure/Persistence/Migrations/**` | **Never mergeable** | See [below](#what-cannot-be-merged-cleanly-ever). |
| **The sample slice** | `Item` — `src/backend/src/*/Items/`, `src/frontend/libs/feature-items/` | **Never mergeable** | You were told on day one to delete it. If you kept it, upstream changes to it are noise; if you deleted it, they are conflicts against nothing. |
| **Project identity** | `CLAUDE.md`, `README.md`, `docs/architecture.md` | **Effectively unmergeable** | Project-specific by design. `CLAUDE.md` describes *your* stack and conventions. Read the upstream diff for ideas; never apply it. |

Two notes on that table. First, `docs/` splits: the process pages (this one, `workflow.md`,
`definition-of-done.md`, `onboarding.md`, the templates) are shared, while `docs/adr/`,
`docs/specs/`, and `docs/incidents/` fill up with *your* content and must never be overwritten
wholesale. Second, the repository-level configuration files — `.gitattributes`, `.vscode/`,
`.github/dependabot.yml`, `.trivyignore.yaml`, `sonar-project.properties`,
`SonarQube.Analysis.xml`, the root `docker-compose.yml` — all belong in the first row, along with
anything similar you add later. They are configuration rather than product, and they are the
files where taking the upstream version wholesale is almost always right.

The reason this taxonomy pays for itself: **row one is where nearly all the ongoing value is, and
it is the row with almost no conflicts.** Most teams who think an upgrade is impossible are
imagining row three.

---

## Strategy 1 — cherry-pick by directory

**Recommended default.** This treats the boilerplate as a catalogue you shop from, not a parent
you inherit from. That matches what it actually is.

Add it as a remote once. Mark it read-only so nobody pushes to it by reflex:

```bash
git remote add boilerplate https://github.com/<your-org>/aj-boilerplate-fs.git
git remote set-url --push boilerplate DISABLED
git fetch boilerplate --no-tags
```

`--no-tags` matters: the boilerplate's release tags (`v1.4.0`) will collide with your own tags of
the same name and you will not notice until a release script picks the wrong one. Fetch tags only
when you deliberately want them, into their own namespace:

```bash
git fetch boilerplate 'refs/tags/*:refs/tags/boilerplate/*'
```

Now review before you take anything. Always scope the diff to a path:

```bash
# What changed in the harness since you cloned?
git diff HEAD boilerplate/main -- .claude/

# Just the file list, to decide what is worth reading
git diff --stat HEAD boilerplate/main -- .claude/ .github/workflows/
```

Then take the paths you want, whole:

```bash
git checkout boilerplate/main -- .claude/hooks/
git checkout boilerplate/main -- .github/workflows/backend-ci.yml
```

That stages the upstream version of those paths in your working tree. It is a **replace, not a
merge** — anything you had changed in `.claude/hooks/` is gone. That is usually what you want for
row-one files and never what you want anywhere else. Check what you are about to lose first:

```bash
git diff boilerplate/main HEAD -- .claude/hooks/    # your local deviations, if any
```

For a file you *have* customised, apply the upstream change as a patch instead, so your edits
survive the parts that do not collide:

```bash
git diff HEAD boilerplate/main -- .github/workflows/frontend-ci.yml > /tmp/fe-ci.patch
git apply --3way /tmp/fe-ci.patch     # leaves conflict markers you resolve by hand
```

Then run the gate before you commit. An upstream hook or workflow can be correct upstream and
wrong here — a hook that assumes a project name, a workflow that assumes a variable you have not
set. See [definition-of-done.md](definition-of-done.md); the bar does not drop because the change
came from the boilerplate.

**Why this is the default:** it is incremental, each step is independently reviewable, it never
produces a conflict you cannot walk away from, and it makes you say out loud which files you are
adopting. The cost is that it is manual and nothing tells you when upstream has moved — which is
what [CHANGELOG.md](../CHANGELOG.md) is for.

---

## Strategy 2 — a genuine merge with an unrelated-history graft

You can force git to merge the two trees:

```bash
git fetch boilerplate --no-tags
git merge boilerplate/main --allow-unrelated-histories
```

Understand what this does before you run it. With no common ancestor, git has nothing to compare
against, so **every file that differs between the two trees becomes a conflict** — not just the
ones either side changed meaningfully. On a repository with a real product in `src/`, that is
hundreds of conflicted files, most of them your own code conflicting with a sample you deleted.
Resolving them is not review; it is clicking "keep mine" several hundred times, during which you
will keep something you meant to take.

It also works **exactly once**. After the merge, you do have a shared ancestor and subsequent
merges are ordinary — which is the entire argument for doing it. But the price of that first merge
scales with how much you have built.

**Do it only if** you cloned recently (weeks, not months), your product code is still thin, and
you genuinely intend to track the boilerplate long-term. Otherwise: do not. Most teams reading
this page are past the point where this pays, and the honest recommendation for them is Strategy
1.

If you do it, do it on a branch, and never on a Friday.

---

## Strategy 3 — vendor only the harness

Copy `.claude/` and `.github/workflows/` wholesale on each boilerplate release. Accept nothing
else, ever.

```bash
git fetch boilerplate --no-tags
git checkout boilerplate/main -- .claude/ .github/workflows/
git status                                  # read every changed path before committing
```

Cheapest, safest, and lowest value. You get the hooks, the standards, the commands, and the CI
definitions — which is a real and continuing benefit, since that is the part of the boilerplate
that keeps improving after you stop looking at it. You get nothing from architecture changes,
infrastructure fixes, or the module improvements.

The caveat: "wholesale" means you must not have local edits to those directories, or you will lose
them silently each time. If you need a local deviation in a hook, keep it in a separate file the
upstream copy does not own, and record why in your own ADR series. A team that customises
`.claude/` in place has quietly opted out of this strategy without noticing.

---

## Strategy 4 — re-derive

Clone the current boilerplate fresh, and port your feature slices into it.

This sounds absurd and is occasionally correct. It is the right call when the boilerplate has
changed **structurally** rather than incrementally:

- A layer moved or was renamed, so every path in your tree is wrong relative to upstream.
- The response envelope or error contract changed — see
  [ADR-0005](adr/0005-apiresponse-envelope-and-status-code-contract.md) for the kind of decision
  that would do this.
- The authentication or authorization model changed.
- The frontend workspace layout changed — apps and libraries reshuffled, Nx boundaries redrawn.

In those cases a merge is not conservative, it is a slow-motion rewrite with conflict markers. A
re-derivation is the same rewrite done deliberately, in an order you choose, with a working tree
that is green at every step.

The economics are simple and worth doing on paper: re-derivation costs roughly one port per
feature slice, and a merge costs roughly one resolution per conflicted file. If you have four
slices and eleven hundred conflicts, the arithmetic is not close.

It is only viable while the accumulated product code is small. If it is not small any more, you
are not upgrading the boilerplate — you are refactoring your own application, and it should be
specced and planned like any other change. That is not a failure; it is what "starting point"
meant all along.

---

## Choosing

| Your situation | Strategy |
|---|---|
| Normal case: real product, want the harness and CI improvements | 1 — cherry-pick |
| Cloned weeks ago, thin product, intend to track upstream | 2 — graft merge, once |
| Want the guardrails maintained, nothing else | 3 — vendor the harness |
| Upstream changed structurally, your product is still small | 4 — re-derive |
| Upstream changed structurally, your product is large | None of these — spec it as a refactor |

---

## What makes your next upgrade easier

Addressed to you, the consumer, on day one rather than in month six. All four of these cost
nothing now and are unrecoverable later.

**Keep the sample slice's shape when you write your own.** The `Item` slice exists to demonstrate
a shape: where the entity lives, where validation lives, how the controller maps, how the frontend
library is laid out. Delete the slice, keep the shape. When upstream changes how a layer works,
a tree that still follows the shape can take the change; a tree that invented its own layout
cannot, and no diff will tell you why.

**Do not edit shared files "just a bit".** A three-line tweak to a hook or a workflow converts a
`git checkout boilerplate/main -- <path>` into a hand merge, every single time, forever. If you
must deviate, deviate in a new file rather than in the upstream one, and say so.

**Record deviations in your own ADR series.** Per
[docs/adr/README.md](adr/README.md), the ADRs that ship are the boilerplate's decisions; yours
start at the next free number after them — or you delete them and start at `0001`, but pick one
and be consistent. Do not hardcode a starting number from memory: count what is in
`docs/adr/` at the version you cloned, because the boilerplate adds ADRs over time and a number
that was free at one release is taken at the next. An ADR that says "we replaced the envelope handler because X" is the document
that tells a future upgrader *not* to take the upstream version of that file. Without it they will
take it, and the reason it was changed will be rediscovered as an incident.

**Write down the commit you started from.** This is the single highest-value item on the page and
the one most consistently skipped. Most upgrade pain is not knowing your own baseline: without it
you cannot diff, cannot read a changelog usefully, and cannot tell whether a file is stale or
deliberate. A `BOILERPLATE_VERSION` file at your repository root with the tag and the SHA is
enough:

```
aj-boilerplate-fs v1.2.0
commit 8f3c1ad4b2e7c9f05a1e6d8b4c2a90fe3d71b6c8
cloned 2026-02-11
```

Update it whenever you adopt an upgrade, and note what you took. Two lines of bookkeeping now
replaces an afternoon of archaeology later.

---

## Knowing what changed upstream

- **[CHANGELOG.md](../CHANGELOG.md)** — the human-readable record of what moved between releases.
  Read this first; it is written to tell you whether an upgrade is worth your time.
- **Release tags** — releases are tagged `vMAJOR.MINOR.PATCH`. Diff between two of them to see the
  whole change set for a path:

  ```bash
  git fetch boilerplate 'refs/tags/*:refs/tags/boilerplate/*'
  git diff --stat boilerplate/v1.1.0 boilerplate/v1.2.0 -- .claude/ .github/
  ```

- **[The ADR index](adr/README.md)** — the record of decisions. A new ADR upstream is the strongest
  possible signal that something structural changed, and it will tell you what was rejected as
  well as what was chosen. If a new upstream ADR contradicts one of your own, that is a decision
  for you to make explicitly rather than a merge to perform.
- **[docs/api/README.md](api/README.md)** — if the contract procedure or the envelope changed, this
  is where the consequence is described.

A commit log is not a substitute for any of these. "42 commits since v1.1.0" tells you nothing
about whether you should care.

---

## What cannot be merged cleanly, ever

Name these explicitly so nobody burns an afternoon proving it.

**EF Core migration history.** `src/backend/src/AjBoilerplate.Infrastructure/Persistence/Migrations/`
cannot be merged, for two independent reasons. First, migrations are ordered by a timestamp
prefix, so two trees' migrations **interleave** rather than append — an upstream migration dated
before yours would have to run after yours, which is not a thing EF Core supports. Second,
`AppDbContextModelSnapshot.cs` is a single generated file describing the whole model, so it
conflicts on every upgrade and cannot be resolved by choosing sides; a half-merged snapshot
generates a wrong next migration, silently. Take the *schema intent* from upstream, apply it to
your model, and generate your own migration from your own model — which is the rule anyway, per
[workflow.md](workflow.md): the migration is generated from the model, never hand-authored to lead
it. Note also that `protect-files.sh` refuses writes to existing migrations and to the snapshot,
so the harness will stop you doing this even if you try.

**`package-lock.json`.** Regenerate, never merge. A merged lockfile is not a lockfile — it is a
plausible-looking file that may describe a dependency graph npm would never have resolved. Take
the upstream `package.json` change if you want it, delete your lock, and run `npm install` to
produce a real one.

**Generated OpenAPI types.** `src/frontend/libs/data-access/api-types/` is generated output, from
*your* API rather than the boilerplate's. Merging it means importing type definitions for
endpoints you do not have. Regenerate with `npm run generate:api` — see
[docs/api/README.md](api/README.md).

**Anything you were told to delete on day one.** The `Item` slice, both halves. If it is still
there, upstream changes to it are noise you will spend real time reviewing. Delete it, then this
category is empty. (The "What's new" module is the deliberate exception — it is not a sample, it
is meant to stay. See [whats-new.md](whats-new.md).)

**`CLAUDE.md`.** It describes your project. Read the upstream diff for ideas — a new rule worth
having, a command that changed — and type the change yourself.

---

## If you are the boilerplate maintainer

Everything above is downstream of decisions you make upstream. Four of them determine whether
consumers can upgrade at all.

**Keep shared and project-specific files separate, physically.** Every time a shared file grows a
project-specific line, you have converted a `git checkout` into a hand merge for every consumer,
forever. This is the same constraint [ADR-0006](adr/0006-three-repository-split.md) already
imposes on documentation — one page per stack, so derivation deletes whole files rather than
editing paragraphs. Apply it to configuration too.

**Never mix a harness change and an application-source change in one commit.** A consumer running
Strategy 1 wants to take your hook fix and not your sample-entity refactor. If they are the same
commit, they cannot, and the honest options are both bad. Separate commits, separate pull
requests, ideally separate releases.

**Tag releases.** `vMAJOR.MINOR.PATCH`, and mean it: bump MAJOR when a consumer has to change
their own code to take the release. An untagged boilerplate cannot be diffed against, which means
it cannot be upgraded from — only cloned again.

**Write the changelog entry as an upgrade instruction, not as a list of commits.** A consumer does
not need to know that `run-affected-tests.sh` was refactored. They need to know: *this changed,
here is whether it affects you, here is what to run.* One line of "if you customised X, do Y"
saves every consumer the same hour. This is the maintainer's highest-leverage writing, and it is
the entry that gets skipped when a release is rushed.

The general principle behind all four: **the maintainer pays once, every consumer pays every
time.** Any cost you can move upstream, move upstream.

---

## Where to look next

| Topic | Path |
|---|---|
| What changed between releases | [../CHANGELOG.md](../CHANGELOG.md) |
| Why the tree is shaped this way | [architecture.md](architecture.md) |
| Why history is not shared | [ADR-0006](adr/0006-three-repository-split.md) |
| The decisions you may be inheriting | [adr/README.md](adr/README.md) |
| The bar any adopted change must still clear | [definition-of-done.md](definition-of-done.md) |
| How to contribute a change back upstream | [../CONTRIBUTING.md](../CONTRIBUTING.md) |
