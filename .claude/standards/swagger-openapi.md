# Standard: Swagger / OpenAPI

OpenAPI is a first-class deliverable: it documents the API for humans and is the **source of
truth for frontend type generation**.

## Rules

- **Enable Swagger UI for Development and Test** (and internal Staging if useful). Do **not**
  expose Swagger UI publicly in Production; the OpenAPI JSON is still generated for the
  build/type-generation pipeline.
- **Document every endpoint:** summary, description, parameters, auth requirement, and every
  response status it can return.
- **Document request and response models** — including the `ApiResponse<T>` envelope and
  `PagedResponse<T>` (see [`api-response-format.md`](api-response-format.md)).
- **Document error responses** (400 / 401 / 403 / 404 / 409 / 410 / 429 / 500 / 503) with
  their `code` values.
- **Document auth requirements** — register the OIDC/bearer security scheme and mark protected
  endpoints. Any separate scoped-token scheme is documented as its own security scheme.
- **Document API versions** as separate OpenAPI groups (`v1`, `v2`) — see
  [`api-versioning.md`](api-versioning.md).
- **Enable XML comments:** set `<GenerateDocumentationFile>true</GenerateDocumentationFile>`
  and feed the XML in. Suppress `CS1591` only for generated or trivial members, never as a
  blanket.
- **The document must be clean enough for client generation** — every schema named, no
  anonymous or duplicated types, nullability accurate, enums emitted as named strings.

## Frontend type generation

- Frontend types are generated from OpenAPI into `libs/data-access/api-types`
  (`openapi-typescript`). Hand-written HTTP clients and hand-copied DTOs are **prohibited**
  (see [`angular.md`](angular.md)).
- Regeneration is a command, not a manual edit: run `/sync` after any API surface change.
- The committed OpenAPI snapshot lives in `docs/api/` so contract drift is visible in the
  diff. A pull request that changes a controller signature but not the snapshot is incomplete.

## Setup checklist

- `AddEndpointsApiExplorer()` + `AddSwaggerGen()` (or the built-in OpenAPI document provider).
- One document per API version via the versioned API explorer group names.
- Security definitions registered for every auth mechanism the API accepts.
- `[ProducesResponseType(typeof(ApiResponse<T>), StatusCodes.Status200OK)]` plus the error
  variants on every action.

## Related

[`api-versioning.md`](api-versioning.md) · [`api-response-format.md`](api-response-format.md) · [`../commands/sync.md`](../commands/sync.md)
