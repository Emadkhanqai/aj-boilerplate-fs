# Workflow: Pre-Push Quality Gate

> **Model routing (do first):** see [`../model-routing.md`](../model-routing.md). Final review
> judgement → frontier tier; the static-analysis *fixes* it produces → workhorse tier.

**Mandatory before every push.** The slash-command form is
[`/pre-push`](../commands/pre-push.md); the full local sweep is
[`/qa`](../commands/qa.md).

The [`sonar-pre-push`](../hooks/sonar-pre-push.sh) hook enforces the SonarQube half of this
automatically — it is a gate, not a reminder.

## Steps (stop and fix on the first hard failure)

```bash
# 1. Understand the change
git status
git diff --stat

# 2. Backend
cd src/backend
dotnet restore
dotnet build --no-incremental          # zero errors; warnings are errors
dotnet format --verify-no-changes      # style gate
dotnet test                            # unit + integration + architecture
dotnet list package --vulnerable --include-transitive

# 3. Frontend
cd ../frontend
npx nx run-many -t lint typecheck test build
npx nx e2e web-e2e                     # when a route or journey changed
npm audit --audit-level=high

# 4. Secrets
gitleaks detect --no-banner --redact

# 5. SonarQube
dotnet sonarscanner begin \
  /k:"$SONAR_PROJECT_KEY" \
  /d:sonar.host.url="$SONAR_HOST_URL" \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.cs.opencover.reportsPaths="**/coverage.opencover.xml"
dotnet build --no-incremental
dotnet test --collect:"XPlat Code Coverage"
dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"
```

## Reading the SonarQube result

Prefer the SonarQube MCP tools:

- `get_project_quality_gate_status`
- `search_sonar_issues_in_projects` filtered to `BLOCKER,CRITICAL,MAJOR`
- `search_security_hotspots`

For a pull request use `list_pull_requests` + `pullRequest`; for a branch use `list_branches` +
`branch`. **Never pass both.**

## Gate rule

- **Any open Blocker / Critical / Major → the push is BLOCKED.** Fix, rerun build and tests,
  rerun the scanner, repeat until zero remain.
- Coverage on new code ≥80%.
- Minor / Info → triage and record; not blocking.
- **Never suppress an issue to pass the gate** without explicit approval and a recorded reason.

## Documentation check

Before declaring the gate green, confirm the docs caught up: the OpenAPI snapshot in
`docs/api/`, a new ADR in `docs/adr/` if a decision was made, and `CLAUDE.md` if a convention
changed. The [`session-handoff`](../hooks/session-handoff.sh) hook flags this drift — read its
report rather than ignoring it.

## After the gate is green

1. Produce a gate report: git status, build, format, tests, frontend results, dependency
   audit, secret scan, SonarQube status, remaining risks.
2. Suggest a commit message.
3. **Ask the user for explicit approval to push.** Approval is per-push and non-transferable —
   see [`../standards/git-approval-policy.md`](../standards/git-approval-policy.md).
