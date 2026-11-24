# Infrastructure — Google Cloud (Terraform)

Provisions the API, the Auth service and the web gateway, and everything they depend on. The
Azure tree ([`infra/azure/`](../azure/README.md)) provisions the API only; **this is the only tree
that deploys the Auth service and the web container.** See
[ADR-0002](../../docs/adr/0002-dual-cloud-provider-behind-one-switch.md) for the provider switch and
[ADR-0012](../../docs/adr/0012-gateway-session-auth.md) for the sign-in model.

| Resource | What it is for |
|---|---|
| Cloud Run service `web` | The SPA and the nginx gateway. The only thing a browser reaches |
| Cloud Run service `auth` | The Auth service: sign-in, sessions, the gateway's `auth_request` |
| Cloud Run job `auth-migrate` | Applies the Auth service's EF Core migrations, before each Auth roll-out |
| Cloud Run service `api` | The API |
| Cloud SQL for SQL Server | One instance, two databases (the API's and the Auth service's), private IP only |
| Memorystore for Redis | Distributed cache and the Auth service's session cache |
| Secret Manager | Every credential, read by reference. Nothing is written into an environment block |
| Artifact Registry | The images Cloud Run runs |
| Serverless VPC Access connector | Lets `api`, `auth` and the job reach the private database and cache |
| Service accounts | One each for `api`, `auth`, `auth-migrate`, `web` and CI, each reading only its own secrets |
| Workload identity pool + provider | How CI authenticates, with no stored key |

**Keycloak and the identity provider (Entra ID) are not deployed.** They are existing external
services. This tree is given their addresses and client ids as variables and reads their client
secrets from Secret Manager secrets you create first. It never creates, configures or changes
either.

This is a boilerplate, not a platform. It is deliberately small enough to read in one sitting.
Extend it; do not treat it as complete.

> **Not verified.** No `terraform apply`, no image build against a registry, no Cloud Run deployment
> and no sign-in through the deployed gateway has been run for this tree. What was run, and what each
> result does and does not show, is listed under [What is and is not verified](#what-is-and-is-not-verified).

## How the pieces call each other

```
browser ──https──▶ web (nginx, public)
                     │  auth_request  GET /auth/validate        X-Serverless-Authorization: Bearer <ID token for auth>
                     ├────────────────────────────────────────▶ auth   (IAM: only web's service account may invoke)
                     │  /api/  + Authorization: Bearer <Keycloak JWT>
                     │                                          X-Serverless-Authorization: Bearer <ID token for api>
                     └────────────────────────────────────────▶ api    (IAM: only web's service account may invoke)
auth ──▶ identity provider, Keycloak (external)      auth, api ──▶ Cloud SQL, Memorystore (private)
```

**Service to service.** `api` and `auth` are not publicly invokable. Cloud Run answers 403 to any
caller without a Google ID token for an identity in their invoker list, which is `web`'s service
account and nobody else (the deploy's read-back fails if `allUsers` or `allAuthenticatedUsers` is
there). The gateway gets those tokens from the metadata server, one per target, and sends each as
`X-Serverless-Authorization`. That header and not `Authorization`, because Cloud Run strips it and
leaves `Authorization` free for the Keycloak JWT the API needs. The mechanism is
`UPSTREAM_AUTH=cloud-run-id-token` in the web image (`src/frontend/docker/njs/token.js` and
`src/frontend/docker/nginx/upstream/cloud-run/`); the default `none` is unchanged behaviour for
compose and for any platform that does not need it.

Why IAM and not `ingress = internal`: a Cloud Run service reaches another's `.run.app` address over
Google's public front end, which `internal` ingress refuses unless the caller's egress is routed
through the VPC with Private Google Access. That needs `ALL_TRAFFIC` egress on the gateway and a
subnet setting this tree does not own, and an unrouted `ALL_TRAFFIC` hangs rather than fails (there
is no Cloud NAT here). IAM needs neither and fails closed. Adding `internal` ingress later is a
hardening step on top of the IAM binding, never a replacement for it.

**Costs and limits of that choice, stated so nobody finds them in production.**

- `web` runs with **CPU always allocated and at least one instance** (`cpu_idle = false`,
  `web_min_instances >= 1`). The ID tokens are refreshed by a timer inside nginx, and a throttled
  instance runs no timers: after an idle hour its tokens would have expired and the first request
  back would be answered 403 by Cloud Run IAM, which reads as "not signed in". The instance is
  billed while idle. `web_min_instances` is validated to stay at or above 1.
- The gateway's rate limits key on the peer address. Behind Cloud Run that is Google's front end, so
  every user shares one bucket until `set_real_ip_from` is configured for it (see
  [troubleshooting](../../docs/auth/troubleshooting.md)).
- The Auth service's in-process sweeper runs on every instance. That it is safe with more than one
  instance running was not tested.

## What you create first (the operator's part)

Terraform reads these by name; it cannot create them, because their values come from registrations
this tree does not own. A missing one fails the plan naming the secret.

1. **Identity provider app registration** (Entra ID: *App registrations*, platform **Web**). Redirect
   URI: **`<public origin>/auth/callback`**, exactly. The public origin is the `web_url` output, or
   `public_origin` when a custom domain or load balancer is in front. API permission: Microsoft
   Graph delegated `User.Read`. Details: [docs/auth/setup.md, section 2a](../../docs/auth/setup.md#2a-entra-id-app-registration).
2. **Keycloak** (existing server): the portal's confidential client with token exchange enabled, its
   client roles, and an identity provider (alias `oidc` by default) that trusts the registration
   above. Details: [section 2b](../../docs/auth/setup.md#2b-keycloak). The client id is
   `keycloak_portal_client_id`; the API accepts only tokens minted for it.
3. **Two Secret Manager secrets with one version each**, in the project, per environment:

   | Secret id (default) | Holds | Override with |
   |---|---|---|
   | `<name_prefix>-<environment>-entra-client-secret` | the app registration's client secret | `entra_client_secret_name` |
   | `<name_prefix>-<environment>-keycloak-client-secret` | the portal client's secret | `keycloak_client_secret_name` |

   ```bash
   printf '%s' "$VALUE" | gcloud secrets create <id> --project=<project> --replication-policy=automatic --data-file=-
   ```

   Read the value from where you hold it; never type it into a command that lands in shell history
   and never put it in a file in this repository.

Everything else the services need is generated by this tree and stored in Secret Manager without
ever being an output: the Auth database login and connection string, the session encryption key
(rotating it signs every user out) and, only when `auth_admin_api_key_enabled` is true, the admin
API key.

---

---

## Before the first use

**Create the state bucket yourself.** This configuration does not create its own state store —
a configuration that manages its own backend cannot bootstrap or destroy itself cleanly. Create
it once, per project, out of band:

```bash
gcloud storage buckets create gs://<your-state-bucket> \
  --project=<your-project-id> \
  --location=<your-region> \
  --uniform-bucket-level-access
gcloud storage buckets update gs://<your-state-bucket> --versioning
```

Then lock it down. **Terraform state contains sensitive values** — the generated SQL password is
in there. Treat the bucket exactly as you would a secret store: private, versioned, encrypted,
and access-audited.

**Bootstrap the CI identity locally.** The workload identity pool is created *by* this
configuration, but CI needs it in order to authenticate. Break the cycle with one local apply
from an account that already has permission, then read the two values CI needs out of the
outputs:

```bash
terraform output workload_identity_provider   # → GCP_WORKLOAD_IDENTITY_PROVIDER secret
terraform output ci_service_account           # → GCP_SERVICE_ACCOUNT secret
```

After that, CI runs every subsequent apply with no stored key.

**The first apply of this tree needs more than CI's roles.** CI's roles are deliberately narrow and do
not include creating service accounts or changing project IAM, and this tree creates five service
accounts and their bindings. So the first apply, and any later apply that adds an identity, is a local
apply by an administrator, the same bootstrap as above. Do not widen CI to avoid it.

**Images before services, and why `terraform apply` alone is not enough on a new environment.** Cloud
Run will not create a service whose image does not exist, and the registry that holds the image is
created by this tree. The deploy workflow therefore applies the registry first (`-target`), builds and
pushes `auth`, `auth-migrate` and `web`, runs the migration job, and only then applies everything.
Locally, on a new environment, do the same:

```bash
terraform apply -target=google_project_service.required -target=google_artifact_registry_repository.containers
# build and push the three images (see "The images" below), then:
terraform apply -target=google_cloud_run_v2_job.auth_migrate
gcloud run jobs execute <prefix>-<environment>-auth-migrate --region=<region> --wait
terraform apply
```

---

## Plan and apply

```bash
cd infra/gcp

# One backend per environment, via prefix. Re-run init when you switch environments.
terraform init \
  -backend-config="bucket=<your-state-bucket>" \
  -backend-config="prefix=dev"

terraform fmt -check -recursive
terraform validate

cp terraform.tfvars.example terraform.tfvars   # then edit it — it is gitignored
terraform plan -out=tfplan

# Read the plan. Actually read it.
terraform apply tfplan
```

In CI the variables arrive as `TF_VAR_project_id`, `TF_VAR_region`, and `TF_VAR_environment`
from repository variables, and there is no `.tfvars` file at all.

---

## The images and the deploy workflow

Three images are built from the commit being deployed:

| Image | Built from | Notes |
|---|---|---|
| `auth` | `src/backend/src/AjBoilerplate.Auth/Dockerfile`, context `src/backend` | the `runtime` stage |
| `auth-migrate` | the same Dockerfile, `--target build` | the only stage that carries `dotnet-ef`; the runtime image cannot migrate |
| `web` | `src/frontend/Dockerfile` | the SPA and the nginx gateway |

The API image is **not** built by the workflow: `API_IMAGE` is a variable you set, as before.

`.github/workflows/deploy-environment.yml` (the `gcp` job) rolls an environment out in this order,
and the order is the design:

1. **registry**, so the images have somewhere to go;
2. **images** built and pushed, and their digests kept;
3. **`auth-migrate`** updated and executed, and the deploy waits for it. A new Auth revision must
   never start against a schema it has not seen. The API's migrations are applied separately, by the
   `migrate` job, from the bundle Backend CI builds;
4. **one plan and one apply.** Terraform's graph puts `api` and `auth` first and `web` last, because
   `web` names both their addresses. The gateway, which every portal path crosses, goes out after what
   sits behind it, so a fault in it is the only thing that changed;
5. **read-back**: `scripts/readback.sh` reads each live service and fails the deployment unless
   ingress, egress and the VPC connector are as intended, the running revision's image digest equals
   the digest pushed in step 2 (`auth`, `web`), and `api` and `auth` have no `allUsers` or
   `allAuthenticatedUsers` invoker. It prints what it found, including `maxScale`, so the log holds
   the quoted values.

Repository (or environment) variables, none secret: `GCP_PROJECT_ID`, `GCP_REGION`, `TF_STATE_BUCKET`,
`NAME_PREFIX`, `API_IMAGE`, `ENTRA_AUTHORITY`, `ENTRA_CLIENT_ID`, `KEYCLOAK_BASE_URL`, `KEYCLOAK_REALM`,
`KEYCLOAK_PORTAL_CLIENT_ID`; optional: `PUBLIC_ORIGIN`, `ENTRA_SCOPES`,
`ENTRA_REQUEST_OWN_CLIENT_ID_SCOPE`, `ENTRA_CLIENT_SECRET_NAME`, `KEYCLOAK_SUBJECT_ISSUER`,
`KEYCLOAK_ISSUER`, `KEYCLOAK_JWKS_URL`, `KEYCLOAK_CLIENT_SECRET_NAME`, `AUTH_ADMIN_API_KEY_ENABLED`.
`NAME_PREFIX` must be set: the workflow derives the registry and job names from it.

---

## Environments

One configuration, three state prefixes: `dev`, `staging`, `prod`. The `environment` variable
drives the differences rather than a separate copy of the code per environment:

| | dev / staging | prod |
|---|---|---|
| Cloud SQL availability | Zonal | Regional |
| Memorystore tier | Basic | Standard (HA) |
| Backup retention | 7 | 30 |
| Deletion protection | off | **on** |

Each environment needs its **own non-overlapping `connector_cidr`**. Two environments sharing a
/28 is the most common failure on a second apply.

---

## Security notes

- **No long-lived credentials.** CI authenticates by workload identity federation. There is no
  service-account key to create, store, rotate, or leak — and none should ever be added.
- **The federation is repository-scoped.** `attribute_condition` pins the pool provider to one
  `owner/repo`. Without it, any repository on GitHub could request a token for your project.
  Never relax it to a wildcard.
- **The API and the Auth service accept no anonymous caller.** Their invoker list is the web service
  account. `web` has `allUsers` because browsers must reach it; nginx authenticates every gated
  request itself. Where policy forbids `allUsers`, set `web_allow_unauthenticated = false` and put a
  load balancer in front.
- **The database has no public IP.** `ipv4_enabled = false`, reachable only from the VPC.
  Turning it on "just to debug" defeats the design; use the Cloud SQL auth proxy instead.
- **Secrets are never in this repository.** The SQL password is generated by Terraform, written
  to Secret Manager, and injected into Cloud Run at start. It is never an output, never in a
  variable file, and never in a log.
- **CI's roles are deliberately narrow.** Widen them with a reason, never to `roles/owner` or
  `roles/editor`.

---

## What is and is not verified

Run for this tree, with what each result does and does not show:

| Check | Result | Shows | Does not show |
|---|---|---|---|
| `terraform fmt -check -recursive`, `terraform init -backend=false`, `terraform validate` (1.9.8, provider ~> 6) | pass; `fmt` and `validate` each failed on a planted error | the configuration parses and type-checks | that any resource can be created |
| `actionlint` on the three workflows | pass, with a planted error failing | the YAML and expressions are well formed | that any step works on a runner |
| `nginx -t` on the three profiles, in the real image's nginx | pass | the templates render and parse | |
| The gateway in `cloud-run-id-token` mode against local stubs (a TLS upstream that returns 403 without the right `X-Serverless-Authorization`, a stub metadata server) | both hops carried the right token, `/readyz` held 503 until both existed, an untrusted upstream certificate and an `http://` upstream were refused | the njs, the headers and the TLS verification behave as written | that Cloud Run IAM or the real metadata server accept it |
| `scripts/readback.sh self-test` | pass, with a planted always-pass failing it | the checks can say no | that the field paths match what `gcloud` prints today |

**Not run, and therefore not known:** `terraform plan` or `apply` against a project; any image build
or push; the migration job (the `dotnet-ef` command and its working directory inside the `build`
image, and whether 2 CPU and 2 GiB suffice); Cloud Run accepting the `X-Serverless-Authorization`
header from nginx; the Auth service reaching Cloud SQL with `TrustServerCertificate=False` (the API's
connection string uses the same setting, and whether Cloud SQL's server certificate validates there
was not checked for either); the Auth service reaching Memorystore (it is plain Redis here, as for
the API, with no TLS and no AUTH); an end-to-end sign-in; and the field paths the read-back assumes
in `gcloud run services describe` output. Treat the first deployment as the first test of all of
these, in a dev project.

---

## Making it yours

1. Set `name_prefix` to something short and yours (12 characters or fewer — the VPC connector
   name is capped at 25 and the identity pool ID at 32).
2. Set `github_repository` to your repository.
3. Review the sizing defaults. They suit dev, and they are undersized for real production load.
4. Decide whether the default VPC is acceptable. For anything beyond dev it usually is not —
   create a dedicated network and set `network_name`.
5. Add whatever you actually need: a load balancer, a custom domain, Cloud Armor, log sinks, a
   monitoring workspace, spend alerts.
6. If you are not deploying to Google Cloud, delete this directory.

## Destroying

```bash
terraform destroy
```

Production has `deletion_protection` on for Cloud SQL and Cloud Run, so a destroy will fail
until someone deliberately turns it off. That is the intent.
