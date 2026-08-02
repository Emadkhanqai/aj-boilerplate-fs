# The `.claude/` agentic-development harness

This directory is the headline feature of this boilerplate. Clone the repo, open Claude Code,
and it already knows the standards, the gates, and the guardrails — no onboarding conversation
required.

**It is committed, never gitignored.** Every developer and every agent gets the same rules.

---

## What is in here

| Path | Purpose |
|---|---|
| `standards/` | The engineering standards. One file per topic. Agents read them before acting. |
| `agents/` | Specialist subagent definitions — backend, frontend, review, security, tests, gate. |
| `commands/` | Slash commands: the repeatable workflows (`/spec`, `/task`, `/implement`, `/qa`, …). |
| `workflows/` | Longer-form procedures a command references — new feature, API change, database change, migration, review, gate, release. |
| `templates/` | Copy-paste starting points — ADR, pull request, migration checklist, domain entity, API controller, Angular component. |
| `hooks/` | **Executable guardrails.** Deterministic shell scripts the harness runs automatically. |
| `settings.json` | Wires the hooks, the permission policy, and the plugin dependencies. Team-shared. |
| `model-routing.md` | Which model tier a task warrants, and when to say so. **Enforced** by the `model-routing` hook on every prompt. |
| `../.mcp.json` | MCP servers: Playwright, Chrome DevTools, SonarQube, Context7, and the cloud pair. |

### Commands vs. workflows

A **command** is what you type; a **workflow** is the procedure it follows. `/implement` and
`/new-migration` are the entry points; `workflows/new-feature.md` and
`workflows/ef-core-migration.md` are the full step-by-step behind them. Read the workflow when
you want the reasoning, run the command when you want the work done.

Documentation the harness points at lives outside this directory: `docs/specs/` (specs),
`docs/adr/` (decisions), `docs/api/` (the OpenAPI snapshot), `docs/handoff/` (session
handoffs), and the root plus nested `CLAUDE.md` files.

---

## First-time setup

### 1. Make the hooks executable

Git preserves the executable bit, so a normal clone is already correct. If you copied the
files rather than cloning:

```bash
chmod +x .claude/hooks/*.sh
```

### 2. Trust the workspace — the plugins load themselves

The plugin dependencies are **declared in the committed `.claude/settings.json`**, via
`extraKnownMarketplaces` (which marketplaces exist) and `enabledPlugins` (which plugins are on
for everyone who clones this repo). You do not install them by hand.

| Marketplace (settings key) | Repo | Plugins enabled here |
|---|---|---|
| `superpowers-marketplace` | `obra/superpowers-marketplace` | `superpowers` |
| `anthropic-agent-skills` | `anthropics/skills` | `document-skills` |
| `claude-code-workflows` | `wshobson/agents` | `backend-development`, `frontend-mobile-development`, `unit-testing`, `database-migrations`, `security-scanning` |
| `karpathy-skills` | `multica-ai/andrej-karpathy-skills` | `andrej-karpathy-skills` |
| `mattpocock` | `mattpocock/skills` | `mattpocock-skills` |

The marketplace key is the marketplace's **own declared name**, not its repo name — they
differ for all five. `enabledPlugins` keys are `pluginName@marketplaceName`.

`claude-code-workflows` ships 95 plugins; five stack-relevant ones are enabled to keep context
lean. Browse and enable more with `/plugin`.

#### Expect prompts on first clone — this is not zero-touch

Be honest with yourself about what "automatic" means here:

- **Workspace trust.** The first time you open this repo, Claude Code asks you to trust the
  folder. This is the same gate that governs `.claude/settings.json` itself — until you accept
  it, none of the settings, hooks, or plugin declarations take effect. That prompt exists
  precisely because a repository can otherwise make your machine execute code.
- **MCP servers still need per-server approval.** Code-executing components are restricted
  beyond the trust gate: the servers in `.mcp.json` are proposed, not silently started. Approve
  each one you want.
- Plugins that bundle hooks or MCP servers inherit that same treatment.

So: **the declaration removes the busywork, not the consent.** If you expected no prompts at
all, that expectation was wrong, and it is wrong for a good reason.

#### If auto-load didn't happen

Usually that means the workspace was not trusted, or the settings file was edited after the
session started. Restart Claude Code and accept the trust prompt. If a marketplace still is not
registered, add it manually — this is the fallback, not the normal path:

```
/plugin marketplace add obra/superpowers-marketplace
/plugin marketplace add anthropics/skills
/plugin marketplace add wshobson/agents
/plugin marketplace add multica-ai/andrej-karpathy-skills
/plugin marketplace add mattpocock/skills
```

Then `/plugin install <name>@<marketplace>` for the plugins in the table above, or run
`/plugin` with no arguments to browse.

### 3. Set the environment variables

Never commit any of these.

| Variable | Needed for |
|---|---|
| `SONAR_HOST_URL`, `SONAR_TOKEN` | the SonarQube gate (`sonar-pre-push`, `/qa`, MCP) |
| `SONAR_PROJECT_KEY` | only if it is not in `sonar-project.properties` |
| `CONTEXT7_API_KEY` | the Context7 MCP server |
| `GCP_PROJECT_ID` | when `CLOUD_PROVIDER=gcp` |
| `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID` | when `CLOUD_PROVIDER=azure` |

The SonarQube side targets **Community Build** — the free, self-hosted edition. One
`docker run -d --name sonarqube -p 9000:9000 sonarqube:community` gives you a working server;
[`standards/sonarqube.md`](standards/sonarqube.md) has the five-minute setup, the token steps,
and the list of things Community deliberately does not have (branch analysis, pull-request
decoration, portfolios, the OWASP/CWE report views). Nothing here needs a licence.

### 4. Optional but recommended — the secret pre-commit hook

```bash
ln -s ../../.claude/hooks/secret-scan.sh .git/hooks/pre-commit
```

Install `gitleaks` for the full rule set; without it the hook falls back to a built-in pattern
set that covers the common cases.

---

## The hooks — what fires, and when

All eight consume the hook JSON payload on stdin, degrade gracefully when a tool is not
installed, and are safe to run by hand. Seven of them parse it; `model-routing.sh` only drains
it, because its output is the same on every prompt and parsing would buy a dependency for
nothing.

Note the events, which are not interchangeable: **PreToolUse** can veto an action before it
happens, **PostToolUse** reacts to one that already did, **Stop** runs when the turn ends, and
**UserPromptSubmit** is the one event whose stdout is injected into the model's context — which
is precisely why the routing policy lives there and nowhere else.

| Hook | Event | Blocking? | What it does |
|---|---|:--:|---|
| `model-routing.sh` | UserPromptSubmit (**every prompt**) | **never** | Injects the model-routing policy into context, because `UserPromptSubmit` stdout is one of the few hook outputs the model actually reads. Forces task classification *before* the first tool call and makes the tier recommendation something said out loud. Dependency-free, ~15 lines of output, always exits 0 — see [`model-routing.md`](model-routing.md). |
| `auto-format.sh` | PostToolUse `Edit\|Write` | no | Formats **only** the edited file. `.cs` → `dotnet format` scoped to the nearest `.csproj`. `.ts/.html/.scss/.css/.json/.md` → Prettier, plus `eslint --fix` for TypeScript. |
| `block-dangerous.sh` | PreToolUse `Bash` | **yes (exit 2)** | Refuses `rm -rf`, `git push --force`/`--force-with-lease`, `git reset --hard`, `git clean -fdx`, history rewriting, `DROP DATABASE`/`TRUNCATE`, production connection strings, and production cloud operations. Prints why. |
| `protect-files.sh` | PreToolUse `Edit\|Write` | **yes (exit 2)** | Refuses edits to `.env*` (templates excepted), `appsettings.Production.json`, **existing** EF Core migrations and the model snapshot, `infra/*/prod/**`, `.claude/settings.json`, and key material. |
| `run-affected-tests.sh` | PostToolUse `Edit\|Write` | no (exit 1 on failure) | Runs the touched project's tests immediately — `dotnet test` for `.cs`, `nx test` for `.ts`. Silent when nothing is affected. **Never exits 2**: it surfaces a failure without cancelling the edit. |
| `secret-scan.sh` | PostToolUse `Edit\|Write` + pre-commit | **yes (exit 2)** | Gitleaks on the changed file when installed; otherwise a built-in regex set for API keys, PEM blocks, connection strings with passwords, JWTs, and cloud credentials. Findings are printed redacted. |
| `sonar-pre-push.sh` | PreToolUse `Bash`, only on `git push` | **yes (exit 2)** | Enforces the SonarQube gate. Blocks while **any** Blocker/Critical/Major is open and prints the offending issues. **Fails closed** when SonarQube is unreachable or unconfigured. |
| `session-handoff.sh` | Stop | never | Writes `docs/handoff/<date>-<session>.md` from `git status` and `git diff --stat` — no model call — and flags when `CLAUDE.md`, `docs/adr/`, or the OpenAPI snapshot look stale relative to what changed. **Reports only.** |

### Escape hatches

| Variable | Effect | When it is acceptable |
|---|---|---|
| `SONAR_GATE_SKIP=1` | Bypasses the SonarQube gate | **First-run bootstrap only, and it is a tech-lead decision — not a developer convenience.** Code pushed under the skip has not been analysed and must be scanned before merge. The hook says so loudly every time. |
| `AJ_SKIP_TESTS_HOOK=1` | Skips `run-affected-tests` | While deliberately working on a known-red suite. |
| `AJ_SKIP_FORMAT_HOOK=1` | Skips `auto-format` | While debugging the formatter itself. |
| `AJ_SKIP_HANDOFF_HOOK=1` | Skips `session-handoff` | Throwaway exploratory sessions. |
| `AJ_SKIP_MODEL_ROUTING_HOOK=1` | Silences the per-prompt `model-routing` reminder | You have internalised the routing table and want the context back. **The policy still applies** — only the nudge stops. |
| `SONAR_BRANCH` | Opt-in: adds `&branch=…` to the `sonar-pre-push` queries | **Only on SonarQube Developer Edition or above.** Unset by default, and it must stay unset on Community Build, which has no branches to query. |
| `AJ_HOOK_TEST_TIMEOUT` | Seconds before the test hook gives up (default 180) | Slow suites. |

`block-dangerous.sh` and `protect-files.sh` have **no** bypass. Those operations are human
decisions made deliberately, outside an agent session.

---

## Running the gate

```
/qa          # everything local: build, format, test, lint, typecheck, audit, secret scan, Sonar
/review      # review the diff against the standards
/pre-push    # the full gate + the readiness report
```

Or by hand:

```bash
cd src/backend  && dotnet build --no-incremental && dotnet format --verify-no-changes && dotnet test
cd src/frontend && npx nx run-many -t lint typecheck test build
gitleaks detect --no-banner --redact
```

**A push is never automatic.** Zero Blocker/Critical/Major, then explicit human approval, every
time — see [`standards/git-approval-policy.md`](standards/git-approval-policy.md).

---

## The command flow

```
/spec  ──►  human approves  ──►  /task  ──►  /implement (one task at a time)
                                                  │
                                          /new-migration   (schema changed)
                                          /sync            (API surface changed)
                                                  │
                                          /qa ──► /review ──► /pre-push ──► ask
```

Each step has a longer-form workflow behind it:

| Situation | Workflow |
|---|---|
| Building a new capability | [`workflows/new-feature.md`](workflows/new-feature.md) |
| Changing the API surface | [`workflows/api-change.md`](workflows/api-change.md) |
| Changing the schema | [`workflows/database-change.md`](workflows/database-change.md) |
| Authoring one migration | [`workflows/ef-core-migration.md`](workflows/ef-core-migration.md) |
| Reviewing a diff | [`workflows/code-review.md`](workflows/code-review.md) |
| Before a push | [`workflows/pre-push-quality-gate.md`](workflows/pre-push-quality-gate.md) |
| Shipping to an environment | [`workflows/release.md`](workflows/release.md) |

## Templates

Copy-paste starting points, already carrying the standards:

- [`templates/adr.md`](templates/adr.md) — architecture decision record → `docs/adr/`
- [`templates/pull-request.md`](templates/pull-request.md) — PR description + gate checklist
- [`templates/ef-migration.md`](templates/ef-migration.md) — the by-hand migration review
- [`templates/domain-entity.md`](templates/domain-entity.md) — a domain entity that enforces
  its own invariants
- [`templates/api-controller.md`](templates/api-controller.md) — a thin versioned controller
  with the full status-code surface
- [`templates/angular-component.md`](templates/angular-component.md) — standalone + signals +
  OnPush + PrimeNG + generated types

---

## Adding a standard

1. Create `standards/<topic>.md`. Follow the shape of the existing files: a one-line statement
   of what it governs, then **rules an agent can actually check**, then a `## Related` line
   linking the neighbouring standards.
2. Write rules, not essays. "Every list endpoint is paginated with a server-side cap" is
   checkable. "Be mindful of performance" is not.
3. Link it from the agents that need it (`agents/backend-agent.md`,
   `agents/frontend-agent.md`) and from `commands/review.md` if reviewers should check it.
4. If it changes how the whole repo works, add a line to the root `CLAUDE.md` too — that is
   what agents load first.
5. If it is worth enforcing deterministically, it belongs in a **hook**, not only in prose. A
   rule a machine can check should not depend on an agent remembering it.

---

## The weekly context retro (30 minutes)

The single practice that makes this harness improve instead of rot. Once a week, with whoever
worked with agents that week:

1. **Collect the misses.** Every place an agent got it wrong: a broken convention, a wrong
   pattern, a re-explained rule, a review comment you have now written twice. Yesterday's
   `docs/handoff/` files are the raw material.
2. **For each one, ask the only question that matters:** *what change would have prevented
   this?* Pick exactly one home for the fix:

   | The agent... | Fix belongs in |
   |---|---|
   | didn't know a rule | `CLAUDE.md` (repo-wide) or `standards/` (topic-specific) |
   | knew the rule but skipped a step | a `commands/` workflow |
   | did something that should be impossible | a **hook** |
   | lacked project context | `docs/specs/` or an ADR |
   | used the wrong specialist | an `agents/` description |

3. **Make the change during the retro.** A fix deferred to "later" is a fix that does not
   happen, and the same miss shows up next week.
4. **Prefer a hook to a paragraph.** Prose is advisory; a hook is deterministic. If a rule can
   be checked by a machine, check it by machine.
5. **Delete what is not earning its place.** A standard nobody has consulted in a month is
   context spent for nothing. Removing it makes everything else more likely to be read.

Keep the resulting notes short and in the repo. The measure of success is simple: the same
mistake should not appear in two consecutive retros.
