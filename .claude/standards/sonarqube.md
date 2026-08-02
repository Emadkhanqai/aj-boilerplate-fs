# Standard: SonarQube Quality Gate (Community Build)

**Status:** Enforced · **Applies to:** every push, every agent, every human.

SonarQube is the mandatory quality gate. It runs **before every push**, and its
Blocker/Critical/Major findings **block** the push. The `sonar-pre-push` hook enforces this
automatically — it is not a reminder, it is a gate.

> **This boilerplate targets SonarQube Community Build — the free, self-hosted edition.**
> Everything below works on a server you can start in one `docker run`, with no licence and no
> paid tier. Read [What Community Build does not have](#what-community-build-does-not-have)
> before you assume a feature exists.

## The rules

1. **The scanner runs before every push.** No exceptions. A push proposed without a fresh scan
   is invalid.
2. **Blocker, Critical, and Major issues must be fixed before push.** While any such issue is
   open, the push is blocked.
3. **Minor / Info** issues are triaged: fix if cheap, otherwise record why they are deferred.
   They do not block a push.
4. **New code must meet the coverage threshold** (≥80% on new code) and introduce no new
   security hotspots at Blocker/Critical/Major.
5. **Do not game the gate.** Suppressing, `// NOSONAR`-ing, marking "won't fix", or narrowing
   the scanned scope to pass the gate is prohibited unless the user explicitly approves that
   specific suppression with a documented reason.

## Severity → action

| Severity | Action | Blocks push? |
|---|---|:--:|
| Blocker | Fix now | Yes |
| Critical | Fix now | Yes |
| Major | Fix now | Yes |
| Minor | Triage; fix if cheap | No |
| Info | Triage | No |

## What Community Build does not have

Community Build **analyses exactly one branch** — the project's main/default branch. Every
analysis you send lands on that same project and **overwrites the previous one**. That single
fact drives every rule on this page.

| Feature | Edition needed | Consequence here |
|---|---|---|
| Branch analysis (`sonar.branch.name`, `&branch=`) | Developer Edition and above | Never pass a branch. One project, one branch. |
| Pull-request analysis and PR decoration (`sonar.pullrequest.*`) | Developer Edition and above | **There is no PR decoration.** Sonar comments never appear on a PR. |
| Applications | Developer Edition and above | Not used. |
| Portfolios | Enterprise Edition and above | Not used. |
| Security reports (the OWASP Top 10 / CWE / PCI report *views*) | Enterprise Edition and above | Use the issue list and security hotspots instead — those **are** in Community. |

**This boilerplate deliberately does not depend on any of them.** Nothing in `.claude/` or
`.github/` will break, silently degrade, or need a licence key. If you later buy Developer
Edition, branch and PR analysis are additive — see the opt-in `SONAR_BRANCH` note in
[`../hooks/sonar-pre-push.sh`](../hooks/sonar-pre-push.sh).

What Community **does** have, and what this gate actually relies on: quality gates and quality
profiles, the **new code** period on the main branch, the C# / TypeScript / JavaScript / HTML /
CSS analysers, coverage import, security hotspots, and the Web API the pre-push hook queries.

### Where the pull-request safety net comes from instead

Since Sonar cannot see a pull request, the PR is gated by everything else in CI: build with
warnings as errors, format/lint, unit + integration + architecture tests with a coverage
threshold, Gitleaks secret scanning, the vulnerable-package audit, and CodeQL. SonarQube
analyses `main` after the merge. See [Continuous integration](#continuous-integration).

## Stand up a free local server (5 minutes)

```bash
# 1. Start SonarQube Community Build. Port 9000 is the web UI and the Web API.
docker run -d --name sonarqube -p 9000:9000 sonarqube:community

# 2. Wait ~1 minute, then open http://localhost:9000
#    Log in as admin / admin. It forces a password change on first login — do it.

# 3. Create the project (Projects → Create project → Local project), keeping the
#    project key you will use below. Choose "Use the global setting" for new code.

# 4. Generate a token: My Account → Security → Generate Tokens.
#    Type "Global Analysis Token" (or a project analysis token). Copy it once.

# 5. Export the three variables. NEVER commit them.
export SONAR_HOST_URL="http://localhost:9000"
export SONAR_TOKEN="<the token you just generated>"
export SONAR_PROJECT_KEY="<your project key>"
```

The `sonarqube:community` tag is the rolling Community Build image; it defaults to an embedded
H2 database, which is fine for local use and unsupported for anything shared. Pair it with
PostgreSQL via Docker Compose if the server outlives your laptop.

Stopping it is `docker stop sonarqube`; starting it again is `docker start sonarqube`.

## Configuration

Set once per developer machine / CI runner, never committed:

| Variable | Meaning |
|---|---|
| `SONAR_HOST_URL` | SonarQube server URL, e.g. `http://localhost:9000` |
| `SONAR_TOKEN` | Analysis token — **a secret**, never committed |
| `SONAR_PROJECT_KEY` | Project key, if not in `sonar-project.properties` |

The project key is resolved in this order: `.sonarlint/connectedMode.json` →
`sonar-project.properties` → the CI workflow → `search_my_sonarqube_projects` over MCP.

In GitHub Actions the same three arrive as repository **variables** (`SONAR_HOST_URL`,
`SONAR_PROJECT_KEY`, `SONAR_PROJECT_KEY_FRONTEND`) and one repository **secret**
(`SONAR_TOKEN`). The token is a secret; the rest are not. Both CI jobs skip themselves while
`SONAR_HOST_URL` is unset, so a fresh clone is not red before you have a server — delete that
part of the guard once you do.

## Running the scanner — two scanners, not one

**This trips people up.** C# is not analysed by the generic CLI: the C# analyser only produces
findings when the build runs *inside* a SonarScanner for .NET session, because it needs the
Roslyn compilation. JavaScript/TypeScript needs no such wrapper and uses the plain CLI.

| Code | Scanner | Install |
|---|---|---|
| C# / .NET | `dotnet-sonarscanner` (begin → build → end) | `dotnet tool install --global dotnet-sonarscanner` |
| TypeScript / JavaScript / HTML / CSS | `sonar-scanner` CLI | [SonarScanner CLI](https://docs.sonarsource.com/sonarqube-community-build/analyzing-source-code/scanners/sonarscanner/), or `SonarSource/sonarqube-scan-action` in CI |

Both need a JVM (Java 17+; the .NET scanner's current versions want Java 21).

### Backend (C#)

```bash
dotnet sonarscanner begin \
  /k:"$SONAR_PROJECT_KEY" \
  /d:sonar.host.url="$SONAR_HOST_URL" \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.cs.opencover.reportsPaths="**/coverage.opencover.xml"

dotnet build --no-incremental
dotnet test --collect:"XPlat Code Coverage"

dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"
```

### Frontend (TypeScript)

```bash
npx nx run-many -t test --coverage --coverageReporters=lcov

sonar-scanner \
  -Dsonar.projectKey="$SONAR_PROJECT_KEY_FRONTEND" \
  -Dsonar.host.url="$SONAR_HOST_URL" \
  -Dsonar.token="$SONAR_TOKEN" \
  -Dsonar.sources=src/frontend \
  -Dsonar.javascript.lcov.reportPaths=src/frontend/coverage/lcov.info
```

**No `sonar.branch.name`. No `sonar.pullrequest.*`.** Community rejects them, and even if it
did not, there is only one branch to analyse. Analysis always targets the default branch.

Use a **separate project key for the frontend**. Two different scanners pushing to one
Community project would overwrite each other's analysis on every run, and the second one to
finish would win.

## Continuous integration

The rule that falls out of "one project, one branch, last analysis wins":

> **Run SonarQube analysis only on pushes to the default branch — never from a pull-request
> build.** A PR-triggered analysis would overwrite `main`'s result with the state of an
> unmerged branch, leaving the dashboard describing code that is not on `main`.

Both [`backend-ci.yml`](../../.github/workflows/backend-ci.yml) and
[`frontend-ci.yml`](../../.github/workflows/frontend-ci.yml) guard the Sonar job on
`github.event_name == 'push' && github.ref == 'refs/heads/main'` and say why in a comment.
Pull requests are gated by build, format/lint, tests, the coverage threshold, Gitleaks, the
vulnerable-package audit, and CodeQL — see
[the safety-net note above](#where-the-pull-request-safety-net-comes-from-instead).

The local `sonar-pre-push` hook is what stops a Blocker/Critical/Major reaching the remote in
the first place, which is the half PR decoration would otherwise have covered.

## Reading results (MCP)

The SonarQube MCP server is wired in `.mcp.json`. Preferred read path:

1. Resolve the project key (see above).
2. `get_project_quality_gate_status` — the overall pass/fail.
3. `search_sonar_issues_in_projects` filtered to severities `BLOCKER,CRITICAL,MAJOR`.
4. `search_security_hotspots` for the security review.

**Call every MCP tool WITHOUT a `branch` argument and WITHOUT a `pullRequest` argument.**
Omitting both queries the default-branch analysis, which on Community Build is the only
analysis that exists. `list_branches` and `list_pull_requests` have nothing useful to return
here — do not build a workflow on them.

## Definition of "gate passed"

- Quality gate status is **OK**, **and**
- Zero open issues at Blocker, Critical, or Major severity on the new/changed code.

Only then may a push be *proposed* — and it still requires explicit user approval (see
[`git-approval-policy.md`](git-approval-policy.md)).

## The escape hatch

`SONAR_GATE_SKIP=1` bypasses the `sonar-pre-push` hook. It exists **only** for first-run
bootstrap, before a SonarQube project exists. **Using it is a tech-lead decision, not a
developer convenience** — every use is announced by the hook and should be justified in the
pull request. Code pushed under the skip has not been analysed and must be scanned before
merge.

## Related

[`git-approval-policy.md`](git-approval-policy.md) · [`../commands/quality-gate.md`](../commands/quality-gate.md) · [`../commands/pre-push.md`](../commands/pre-push.md) · [`../workflows/pre-push-quality-gate.md`](../workflows/pre-push-quality-gate.md) · [`../hooks/sonar-pre-push.sh`](../hooks/sonar-pre-push.sh)
