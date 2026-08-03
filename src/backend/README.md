# Backend

.NET 10 / ASP.NET Core, layered Clean Architecture, EF Core + SQL Server.

```
src/
  AjBoilerplate.Domain/          entities, domain exceptions — depends on nothing
  AjBoilerplate.Application/     use cases, ports, validation — depends on Domain
  AjBoilerplate.Contracts/       wire DTOs + ApiResponse<T> — depends on nothing
  AjBoilerplate.Infrastructure/  EF Core, cache, transports, cloud secrets
  AjBoilerplate.Api/             controllers, middleware, auth, DI, observability
tests/
  AjBoilerplate.UnitTests/
  AjBoilerplate.IntegrationTests/
  AjBoilerplate.ArchitectureTests/   enforces the dependency rules above
```

The dependency direction is enforced by `DependencyRuleTests`, not by convention. Note in
particular that **the Api does not reference the Domain**: it works through Application models and
Contracts DTOs, which is what keeps entities out of request and response bodies.

## Run it

```bash
docker network create app-net          # once per host
cp .env.example .env                   # then fill in MSSQL_SA_PASSWORD
docker compose up -d db redis
docker compose --profile tools run --rm migrate     # apply migrations
dotnet run --project src/AjBoilerplate.Api          # http://localhost:5080, Swagger at /swagger
```

## Toolchain

The SDK is pinned by [`global.json`](global.json) (`10.0.100`, `rollForward: latestFeature`) and the
CLI tools by [`.config/dotnet-tools.json`](.config/dotnet-tools.json). Run this once per clone:

```bash
dotnet tool restore                    # dotnet-ef, swagger — at the versions this repo expects
```

`latestFeature` pins the major/minor so a machine with a newer .NET installed cannot silently build
against it, while still accepting SDK patches and feature bands without a repo change. `dotnet-ef` is
pinned to the same EF Core version the projects reference, so the tool that generates a migration is
never a different version from the one that runs it.

**`global.json` is resolved from the current working directory, not from the project path.** Run
`dotnet` commands from `src/backend` (or below). `dotnet build src/backend/...` from the repository
root ignores this file.

## Checks

```bash
dotnet build                           # warnings are errors
dotnet test                            # needs Docker running — see below
dotnet format --verify-no-changes
./scripts/generate-openapi.sh --check  # the committed API contract is current
```

**`dotnet test` requires Docker.** The integration suite starts a real SQL Server container through
Testcontainers, applies the migrations to it, and tears it down; the unit and architecture suites
need nothing. There is no in-memory database provider anywhere in this repository, on purpose: a
suite that cannot see a unique index, a `rowversion`, or the SQL that EF Core actually emits is not
testing the integration. Testcontainers generates the container's password at run time, so no
credential is committed here or in CI.

Adds roughly 10 seconds to a warm run (image cached, container start included) and around a minute
on the first run that has to pull the image.

## Migrations

Schema changes go through EF Core migrations only — never `EnsureCreated`, never hand-written DDL,
and never auto-applied at application startup.

```bash
dotnet ef migrations add <IntentRevealingName> \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output-dir Persistence/Migrations
```

Review the generated `Up`/`Down` before applying. The design-time factory reads its connection
string from `APP_DB_CONNECTION` (not `ConnectionStrings__Default`, which only the running app uses).

### Deploying a schema change

Migrations are **never** auto-applied at application startup. CI builds a
[migration bundle](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles) —
a self-contained executable that applies exactly the migrations in that commit — and uploads it as the
`migration-bundle` artefact alongside the idempotent SQL script. The target needs no SDK, no source
tree, and no `dotnet ef`:

```bash
./migrate --connection "$DB_CONNECTION_STRING"
```

The bundle runs **before** the new application version goes live.

### The zero-downtime rule: expand → migrate → contract

For the length of a deployment, the OLD code and the NEW schema are running at the same time. Every
migration must therefore be compatible with the release that is *currently serving traffic*, not only
with the one being deployed. That splits any destructive change across **three** releases:

| Release | Schema | Code |
|---|---|---|
| **1 — expand** | Add the new column/table. Nullable, or with a default. Backfill it. | Write to both old and new; keep reading the old. |
| **2 — migrate** | No destructive change. | Read from the new. The old column is now written but unread. |
| **3 — contract** | Drop the old column/constraint/table. | Nothing references it, and has not for a whole release. |

**Never ship a destructive migration in the same release as the code that stops using the column.**
The moment the bundle runs, every instance still serving traffic is the previous version — and it is
still selecting that column. Dropping it is an immediate, self-inflicted outage, and rolling the code
back does not fix it, because the data is gone.

The same reasoning applies to renames (a rename is a drop plus an add), to narrowing a type, and to
adding a `NOT NULL` column with no default. Prefer additive changes; when one genuinely cannot be
additive, it is three releases, not one.

## The API contract

`docs/api/openapi.json` is **committed**, and CI regenerates it and fails the build if it differs.
The frontend generates its TypeScript types from that file, so it is the contract — and a contract
that can change without anyone noticing is not one.

```bash
./scripts/generate-openapi.sh            # rewrite the snapshot after an intended change
./scripts/generate-openapi.sh --check    # what CI runs
```

No running server and no database: the script loads the built assembly and asks the same
`ISwaggerProvider` the `/swagger` endpoint uses.

When the check fails, read the diff before doing anything. If the change was intended, regenerate and
commit the snapshot **with** the code change so the contract change is reviewed rather than
discovered; if it was not, fix the API. Never regenerate to make the gate quiet. Breaking-change
rules are in [`docs/api/README.md`](../../docs/api/README.md).

## Idempotency keys

A client can make an unsafe request safely retryable by sending an `Idempotency-Key` header:

```
POST /api/v1/items
Idempotency-Key: <a UUID your client generates once per logical operation>
```

Generate the key once, before the first attempt, and reuse that same value for every retry of
that operation — a key generated per attempt is indistinguishable from no key at all. (The
placeholder above is deliberately not a literal UUID: the repository's secret scan reads a long
opaque token after `Key:` as a credential, and an example that trips your own gate is a bad
example.)

The first request executes and its response is stored; a retry carrying the same key returns that
stored response (with `Idempotency-Replayed: true`) instead of creating a second record. It is
**opt-in per request and POST-only** — traffic without the header is completely unaffected.

| Situation | Result |
|---|---|
| First request | Executes normally |
| Retry, same key, same payload, first one finished | `200`/`201` replay of the original response |
| Retry, same key, first one still running | `409` — retry shortly |
| Same key, **different** payload | `409` — the key was used for a different request |
| First attempt failed (4xx/5xx) | The key is released, so the retry genuinely retries |

Keys are scoped to the **authenticated caller**, so two users choosing the same key never collide and
one can never read back another's response. Only 2xx responses are stored — freezing a transient
failure would make the client's retry key permanently unusable.

Configuration lives under `Idempotency` (`Enabled`, `MaxResponseBytes`, `RetentionHours`). See
[ADR-0009](../../docs/adr/0009-idempotency-keys-for-unsafe-requests.md).

**Records are not pruned automatically.** A boilerplate should not impose a scheduler, so retention is
yours to run — a nightly job against the index that exists for it:

```sql
DELETE TOP (5000) FROM IdempotencyRecords WHERE CreatedAt < DATEADD(hour, -24, SYSUTCDATETIME());
```

## Feature flags

`IFeatureFlags` with a configuration-backed implementation — no third-party dependency, so the
boilerplate does not pick a flag vendor for you.

```jsonc
// appsettings.json — or Features__NewCheckout=true in the environment
"Features": { "NewCheckout": true }
```

```csharp
if (_features.IsEnabled("NewCheckout")) { /* ... */ }
```

Anything unknown, blank, or not a boolean is **off**: the safe direction to be wrong in. Values are
read through `IConfiguration` on every call, so a provider that reloads on change makes a flag
flippable without a redeploy.

A flag is **not** an authorization check — that is `RoleCapabilities` and the policies. And every flag
is a branch that must be tested both ways, so add one with a plan for deleting it.

To move to a real flag platform, implement `IFeatureFlags` over its SDK and change one registration in
`AddInfrastructure`. No call site changes.

## File storage

`IFileStorage` — the counterpart to `IEmailSender`, so a flow that saves an upload is
storage-agnostic. Keys are opaque, relative, and forward-slash separated (`invoices/2026/03/x.pdf`); a
key that escapes its container is rejected rather than normalised.

**Only the local-disk implementation ships.** The cloud arms of the `CLOUD_PROVIDER` switch are a seam,
not an implementation, and they are honest about it:

| Configuration | Behaviour |
|---|---|
| Nothing configured, Development | `LocalFileStorage` — real round trips under `Storage:LocalRoot` |
| Nothing configured, deployed | Registered but throws on first use, naming what to configure |
| `Storage:Gcp:Bucket` / `Storage:Azure:Container` set | **Fails at startup** with the steps to implement it |

Adding a cloud SDK would put a heavy dependency in every consuming project, including those that store
no files — and an untested implementation nobody has run against a real bucket is worse than none,
because it looks finished. Configuring a bucket is a statement of intent this boilerplate cannot
honour, so it says so at startup rather than silently writing to a container filesystem that
disappears on the next deploy.

To implement one: add the provider SDK to `AjBoilerplate.Infrastructure`, implement `IFileStorage` over
it, and register it in `AddStorage`. Nothing that stores a file changes.

## Cloud provider

`CLOUD_PROVIDER` (bound as `Cloud:Provider`) accepts `gcp` (default) or `azure`. It selects two
things and nothing else:

| Concern | `gcp` | `azure` |
|---|---|---|
| Secrets | Secret Manager | Key Vault + Managed Identity |
| Authentication issuer | Google Cloud Identity | Microsoft Entra ID |

Authorization is Keycloak on both and never branches. Neither does the cache: Memorystore and Azure
Cache for Redis speak the same protocol, so one connection string serves both. An unrecognised value
fails at startup rather than silently defaulting.

## The sample slice

`Item` — entity, use cases, validators, EF configuration, repository, `ItemsController`
(`/api/v1/items`), the `Items` table in the `InitialCreate` migration, and its tests — exists only
to prove the path end to end. **Delete or rename it on day one.** Every file in it says so.

## The `Features` module (not a sample)

The other module here is the "What's new" feature spotlight: `Features/` at every layer,
`FeaturesController` (`GET /api/v1/features/unack`, `POST /api/v1/features/ack`), and the
`AddFeatureAnnouncements` migration, which creates `feat_Features` and `feat_Acknowledgements` and
**seeds nothing**. It carries no business domain and is meant to stay — shipping an announcement is
an INSERT-only migration and no code change at all. Full reference:
[`docs/whats-new.md`](../../docs/whats-new.md).
