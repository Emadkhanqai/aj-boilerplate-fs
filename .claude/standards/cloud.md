# Standard: Cloud Platform (`CLOUD_PROVIDER`)

This boilerplate targets **two clouds behind one switch**. Pick one per environment; the
application code stays the same.

## The switch

`CLOUD_PROVIDER` is an environment variable, surfaced in configuration as `Cloud:Provider`.
It accepts exactly `gcp` or `azure` and selects the registration at the composition root.

```csharp
var provider = builder.Configuration["Cloud:Provider"]?.ToLowerInvariant()
    ?? throw new InvalidOperationException("Cloud:Provider (CLOUD_PROVIDER) is required.");

builder.Services.AddCloudPlatform(provider switch
{
    "gcp"   => CloudProvider.Gcp,
    "azure" => CloudProvider.Azure,
    _       => throw new InvalidOperationException($"Unsupported Cloud:Provider '{provider}'.")
});
```

**Fail fast on an unknown or missing value.** Never default silently — a mis-set provider that
falls back to a local stub is how a production deployment ends up reading secrets from a file.

## Service mapping

| Concern | `CLOUD_PROVIDER=gcp` | `CLOUD_PROVIDER=azure` |
|---|---|---|
| API hosting | **Cloud Run** (container) | **App Service** or **Container Apps** |
| Frontend hosting | Cloud Run (container) or static hosting | App Service / Static Web Apps |
| Relational database | **Cloud SQL for SQL Server** | **Azure SQL Database** |
| Cache | **Memorystore for Redis** | **Azure Cache for Redis** |
| Secrets | **Secret Manager** | **Key Vault** |
| Workload credentials | **Workload Identity** (no key files) | **Managed Identity** (no client secrets) |
| Authentication (authN) | **Google Cloud Identity** (OIDC) | **Microsoft Entra ID** (OIDC) |
| **Authorization (authZ)** | **Keycloak** | **Keycloak** |
| Object storage | Cloud Storage | Blob Storage |
| Messaging | Pub/Sub | Service Bus |
| Telemetry backend | Cloud Logging / Monitoring / Trace (OTLP) | Azure Monitor / Application Insights (OTLP) |
| IaC | **Terraform** in `infra/gcp/` | **Bicep** in `infra/azure/` |

### What actually needs a code branch

Only **two** things: the secrets provider and the authentication setup. Everything else is
either protocol-identical or lives entirely in `infra/`.

- **Redis is protocol-identical** — `IDistributedCache` with a Redis connection string works
  on both. The difference is a connection string and an `infra/` resource, not code.
- **SQL Server is the same engine on both** — the connection string shape differs, the EF Core
  provider and every migration do not (see [`mssql.md`](mssql.md)).
- **Secrets** sit behind `ISecretsProvider`, with a `GcpSecretManagerSecretsProvider` and an
  `AzureKeyVaultSecretsProvider`. Application code never references either type directly.
- **Authentication** differs only in the OIDC authority/audience configured for JwtBearer.

### Keycloak is provider-independent

**Keycloak is the authorization layer under both providers.** The cloud identity service
authenticates the human; Keycloak owns roles, scopes, and permissions, and mints any scoped,
time-bound, revocable token the application issues. This is deliberate: changing cloud must
never change the authorization model, and the permission tests must not need rewriting.
Keycloak itself runs as a container on the chosen platform's compute service.

## Rules

- **No secrets in source or in plain-text app settings for any non-local environment.** Use
  the provider's secret store, referenced by name, resolved at startup through
  `ISecretsProvider`.
- **Prefer workload/managed identity over embedded credentials** everywhere the platform
  supports it. A downloaded service-account key file in a repository is a blocking finding.
- **Keep configuration keys at parity across providers and environments.** The same key name
  resolves to the same meaning under `gcp` and `azure`; only the value source differs.
- **`infra/` is reviewed IaC, not applied state.** Nothing in this repository provisions live
  resources on its own; deployment is a pipeline step against a reviewed plan.
- **Never commit a real project id, subscription id, tenant id, resource name, hostname, or
  endpoint.** Use placeholders and supply real values through pipeline variables. The
  `secret-scan` hook and the CI secret job both check this.
- **Production infrastructure paths (`infra/*/prod/`) are protected** — the `protect-files`
  hook blocks agent edits to them, and prod cloud CLI operations are blocked by
  `block-dangerous`. Production changes go through a reviewed pipeline, never an agent
  session.
- When an adapter has no live credentials in the current environment, **still build the
  adapter and its contract tests** behind the port, and classify the missing live proof as
  blocked-on-infrastructure — never as "not needed".

## Local development

Local development targets neither cloud: a local SQL Server, a local Redis, a local Keycloak,
and `dotnet user-secrets`. `CLOUD_PROVIDER` may be unset locally **only** if the composition
root registers an explicit `Local` implementation of every port — and that implementation must
be impossible to select in a deployed environment.

## Related

[`dotnet-security.md`](dotnet-security.md) · [`mssql.md`](mssql.md) · [`observability-tracing.md`](observability-tracing.md) · [`security.md`](security.md)
