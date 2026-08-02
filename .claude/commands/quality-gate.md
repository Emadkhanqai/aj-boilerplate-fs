---
description: Run SonarQube and enforce zero Blocker/Critical/Major before a push may be proposed.
---

# /quality-gate

The focused SonarQube gate. Assumes build and tests are already green — if they are not, run
[`/qa`](qa.md) first.

## Steps

1. Ensure the solution builds so the scanner has fresh analysis input.
2. Run the scanner:
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
3. Read the results over the SonarQube MCP:
   - `get_project_quality_gate_status`
   - `search_sonar_issues_in_projects` filtered to `BLOCKER,CRITICAL,MAJOR`
   - `search_security_hotspots` for the security review
   - Resolve the project key first; for a PR use `list_pull_requests` + `pullRequest`, for a
     branch use `list_branches` + `branch`. **Never pass both.**

## Rules

- **Blocker / Critical / Major must be zero before any push.** Fix → rebuild → rescan →
  repeat. Minor / Info are triaged.
- Coverage on new code ≥80%.
- **Do not suppress an issue to pass the gate** without explicit user approval and a recorded
  reason. `// NOSONAR`, "won't fix", and narrowing the scanned scope all count as suppression.
- If the server is unreachable, the gate has **not** passed. Say so — do not proceed as if it
  had. `SONAR_GATE_SKIP=1` exists only for first-run bootstrap and is a tech-lead decision.

See [`../standards/sonarqube.md`](../standards/sonarqube.md).
