# Standard: API Design

**Style:** RESTful HTTP JSON API on ASP.NET Core. Contracts are shared with the frontend via
OpenAPI.

## Contracts & DTOs

- All request/response bodies are **DTOs in `AjBoilerplate.Contracts`** — pure data, no
  business logic, no domain or EF Core types.
- The **OpenAPI document is the single source of truth** for the frontend. The frontend
  generates its types from it into `libs/data-access/api-types` — **no hand-duplicated
  models** (see [`angular.md`](angular.md) and [`swagger-openapi.md`](swagger-openapi.md)).
- Swagger UI is enabled at least in Development so the frontend can sync.

## Envelope & status codes

- Every response uses the `ApiResponse<T>` envelope with `traceId` — see
  [`api-response-format.md`](api-response-format.md), which also carries the authoritative
  status-code table (200/201/202/204/400/401/403/404/409/410/500/503) and the hide-as-404,
  409-for-optimistic-concurrency, and 410-vs-404 conventions.
- `ProblemDetails` remains in place for framework-level faults.

## Conventions

- Resource-oriented routes: `/api/v1/items`, `/api/v1/items/{id}`,
  `/api/v1/items/{id}/history`.
- Verbs via HTTP methods; use sub-resources or command sub-paths for lifecycle actions
  (`POST /api/v1/items/{id}/archive`).
- Plural nouns, kebab-case paths, camelCase JSON.
- Pagination, filtering, and sorting via query parameters; always return a total count and
  always cap `pageSize`. **No unbounded list endpoint ships.**
- Versioning via a URL segment (`/api/v1/...`) — see [`api-versioning.md`](api-versioning.md).
- `POST` that creates returns **201** with a `Location` header.
- Idempotency keys on non-repeatable commands (see [`middleware.md`](middleware.md)).
- Expose entity ids opaquely where enumerable integer ids would leak volume or enable
  scraping.

## Authorization at the boundary

- Every endpoint declares its required policy. Authorization is enforced **server-side**,
  every time.
- Fields the caller may not see are removed by **projecting a different DTO per permission
  level**, so a disallowed response type cannot structurally carry them. Never rely on the
  client to hide a field (see [`owasp-security.md`](owasp-security.md)).

## Documentation

- XML doc comments feed OpenAPI. Each endpoint documents every status it can return,
  including the error shapes and their `code` values.

## Related

[`clean-architecture.md`](clean-architecture.md) · [`dotnet.md`](dotnet.md) · [`api-response-format.md`](api-response-format.md) · [`security.md`](security.md) · [`angular.md`](angular.md)
