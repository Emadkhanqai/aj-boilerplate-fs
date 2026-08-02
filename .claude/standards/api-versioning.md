# Standard: API Versioning

All public APIs are versioned. Contracts never break silently.

## Rules

- **Every public API endpoint is versioned.** No unversioned public route ships.
- **URL-based versioning is the default:**
  ```
  /api/v1/items
  /api/v1/items/{id}
  ```
- **Breaking changes go to a new major version** (`/api/v2/...`). A breaking change is any
  change that could make an existing, compliant client fail: removing or renaming a field,
  tightening validation, changing a type, changing status-code semantics, or changing the auth
  requirement.
- **Never break an existing contract silently.** Additive, backward-compatible changes (a new
  optional field, a new endpoint) may stay within the current version.
- **OpenAPI exposes versioned groups** — one document group per version (`v1`, `v2`) — so the
  frontend generator targets a specific version.
- **The frontend consumes versioned endpoints only.** No calls to unversioned paths.
- **Deprecation, not deletion:** a superseded version is marked `deprecated: true` in OpenAPI
  and kept for an agreed window before removal, with `Deprecation` / `Sunset` headers emitted
  (see [`middleware.md`](middleware.md)).
- **Any endpoint published to an external consumer is a contract.** Treat a change to it as
  breaking by default, bump the version, and coordinate the cutover — record the decision as
  an ADR in `docs/adr/`.

## Implementation (ASP.NET Core)

- Use `Asp.Versioning.Mvc` + `Asp.Versioning.Mvc.ApiExplorer`.
- Controllers declare their version and route:
  ```csharp
  [ApiController]
  [ApiVersion("1.0")]
  [Route("api/v{version:apiVersion}/items")]
  public sealed class ItemsController : ControllerBase { /* ... */ }
  ```
- Configure:
  ```csharp
  builder.Services
      .AddApiVersioning(o =>
      {
          o.DefaultApiVersion = new ApiVersion(1, 0);
          o.AssumeDefaultVersionWhenUnspecified = false;
          o.ReportApiVersions = true;
      })
      .AddApiExplorer(o =>
      {
          o.GroupNameFormat = "'v'VVV";
          o.SubstituteApiVersionInUrl = true;
      });
  ```
- `ReportApiVersions = true` emits the `api-supported-versions` /
  `api-deprecated-versions` headers.

## Naming

Resource names are plural and kebab-case: `items`, `item-categories`.

## Related

[`api-response-format.md`](api-response-format.md) · [`swagger-openapi.md`](swagger-openapi.md) · [`api-design.md`](api-design.md)
