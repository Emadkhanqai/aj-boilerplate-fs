---
description: Run the mandatory pre-push quality gate and report readiness. Never pushes.
---

# /pre-push

Run the full pre-push quality gate and report. **This command never pushes.**

## Do this, in order (stop and fix on the first hard failure)

1. `git status` and `git diff --stat` — the tree is committed and the change is understood.
2. Run [`/qa`](qa.md) — build, format, test, lint, typecheck, dependency audit, secret scan.
3. Run the **SonarQube scanner** and read the results via the SonarQube MCP:
   `get_project_quality_gate_status`, then `search_sonar_issues_in_projects` filtered to
   `BLOCKER,CRITICAL,MAJOR`, then `search_security_hotspots`.
4. Confirm the documentation caught up with the code: the OpenAPI snapshot in `docs/api/`, any
   new ADR in `docs/adr/`, and `CLAUDE.md` if a convention changed. The `session-handoff` hook
   flags this drift — do not ignore its report.

## Enforce

- Any open **Blocker / Critical / Major** → fix, rerun the build and tests, rerun the scanner,
  and repeat until zero remain.
- Coverage on new code ≥80%.
- Minor / Info → triage and record.
- **Never suppress an issue to pass the gate** without explicit user approval and a documented
  reason.

## Report

Print the real output for: git status, build, format, tests, frontend lint/typecheck/test/
build, dependency audit, secret scan, and the SonarQube gate status with the open
Blocker/Critical/Major count. Then list the remaining risks and suggest a commit message.

**Then ask for explicit push approval** — and do not push. See
[`../standards/git-approval-policy.md`](../standards/git-approval-policy.md).

The full procedure, including the exact commands, is
[`../workflows/pre-push-quality-gate.md`](../workflows/pre-push-quality-gate.md). Use
[`../templates/pull-request.md`](../templates/pull-request.md) for the PR description.
