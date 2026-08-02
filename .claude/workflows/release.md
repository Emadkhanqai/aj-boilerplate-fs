# Workflow: Release

> **Model routing (do first):** see [`../model-routing.md`](../model-routing.md). Release
> *mechanics* → workhorse tier; the go/no-go *risk call* → frontier tier.

Shipping a change to an environment. Nothing here bypasses the approval or SonarQube rules.

## 0. Preconditions

- The feature, API, and database workflows are complete; the branch is up to date.
- Every non-negotiable rule is satisfied: migration-based schema, explicit push approval, a
  green SonarQube gate, no secrets.

## 1. Full verification

Run [`pre-push-quality-gate.md`](pre-push-quality-gate.md) end to end. If SonarQube reports any
**Blocker / Critical / Major**: stop → fix → rerun the scanner → repeat until clean. Minor and
Info may be triaged.

## 2. Summarise & request approval

Provide the change summary, the changed files, build and test results, the SonarQube result,
the remaining risks, and a suggested commit message. **Then wait for explicit user approval.**
No push, force-push, merge, rebase, tag, or release without it
([`../standards/git-approval-policy.md`](../standards/git-approval-policy.md)).

## 3. Database (if the schema changed)

Apply the reviewed idempotent script as a **controlled step** — never on application startup.
Enable maintenance mode during the migration if the change is not backward-compatible
([`../standards/middleware.md`](../standards/middleware.md) §19). For a breaking change, the
release should be the *expand* or the *contract* half of an
expand → migrate → contract sequence, never both at once.

## 4. Deploy

The target depends on `CLOUD_PROVIDER` — see [`../standards/cloud.md`](../standards/cloud.md).

| | `gcp` | `azure` |
|---|---|---|
| API | Cloud Run | App Service / Container Apps |
| Frontend | Cloud Run or static hosting | App Service / Static Web Apps |
| Database | Cloud SQL for SQL Server | Azure SQL Database |
| Cache | Memorystore for Redis | Azure Cache for Redis |
| Secrets | Secret Manager | Key Vault |
| Credentials | Workload Identity | Managed Identity |
| IaC | Terraform, `infra/gcp/` | Bicep, `infra/azure/` |

- Secrets are resolved at startup through `ISecretsProvider` — **never** baked into an image or
  an app setting.
- Configuration keys stay at parity across providers and environments.
- `infra/` is applied by the **pipeline against a reviewed plan**, never from an agent session.
  The [`block-dangerous`](../hooks/block-dangerous.sh) hook refuses production cloud operations
  and `terraform apply` for exactly this reason.
- Promote dev → staging → production. Never deploy straight to production.

## 5. Verify after deploy

- `/health/live` and `/health/ready` both green, including the database and cache checks.
- The versioned API and its OpenAPI document respond; deprecated versions still respond within
  their sunset window.
- Watch error rate and latency in the provider's telemetry backend before declaring success.

## 6. Post-release

Record follow-ups and anything learned in `docs/adr/` (if a decision was made) or the next
session handoff. If the release went wrong, write down what would have caught it — that is the
raw material for the weekly context retro in [`../README.md`](../README.md).
