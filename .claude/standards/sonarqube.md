# Standard: SonarQube Quality Gate

**Status:** Enforced · **Applies to:** every push, every agent, every human.

SonarQube is the mandatory quality gate. It runs **before every push**, and its
Blocker/Critical/Major findings **block** the push. The `sonar-pre-push` hook enforces this
automatically — it is not a reminder, it is a gate.

## The rules

1. **The scanner runs before every push.** No exceptions. A push proposed without a fresh scan
   is invalid.
2. **Blocker, Critical, and Major issues must be fixed before push.** While any such issue is
   open on the changed code, the push is blocked.
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

## Configuration

Set once per developer machine / CI runner, never committed:

| Variable | Meaning |
|---|---|
| `SONAR_HOST_URL` | SonarQube server URL |
| `SONAR_TOKEN` | Analysis token — **a secret**, never committed |
| `SONAR_PROJECT_KEY` | Project key, if not in `sonar-project.properties` |

The project key is resolved in this order: `.sonarlint/connectedMode.json` →
`sonar-project.properties` → the CI workflow → `search_my_sonarqube_projects` over MCP.

## Running the scanner

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

The frontend is analysed by the same scanner run, configured with the TypeScript analyzer and
the LCOV coverage report path.

## Reading results (MCP)

The SonarQube MCP server is wired in `.mcp.json`. Preferred read path:

1. Resolve the project key (see above).
2. `get_project_quality_gate_status` — the overall pass/fail.
3. `search_sonar_issues_in_projects` filtered to severities `BLOCKER,CRITICAL,MAJOR`.
4. For a pull request, discover the PR key with `list_pull_requests` and pass `pullRequest`;
   for a branch, use `list_branches` and pass `branch`. **Never pass both.**

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
