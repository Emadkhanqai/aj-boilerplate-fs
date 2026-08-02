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
dotnet run --project src/AjBoilerplate.Api          # http://localhost:5292, Swagger at /swagger
```

## Checks

```bash
dotnet build                           # warnings are errors
dotnet test                            # needs Docker running — see below
dotnet format --verify-no-changes
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
(`/api/v1/items`), the `InitialCreate` migration, and its tests — exists only to prove the path end
to end. **Delete or rename it on day one.** Every file in it says so.
