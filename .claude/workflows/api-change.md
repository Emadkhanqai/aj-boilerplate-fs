# Workflow: API Change

> **Model routing (do first):** see [`../model-routing.md`](../model-routing.md). API
> *implementation* → workhorse tier; a breaking *contract/versioning* decision → frontier tier.

Changing the API surface (new endpoint, changed DTO, new version). Keeps contracts safe and
the frontend in lockstep.

## 1. Classify the change

- **Additive / backward-compatible** (new endpoint, new *optional* field) → stays in the
  current version.
- **Breaking** (remove or rename a field, tighten validation, change a type, change status-code
  semantics, change the auth requirement) → **a new major version**, `/api/v2/...`. Never break
  a contract silently ([`../standards/api-versioning.md`](../standards/api-versioning.md)).
- **Any endpoint an external consumer already depends on is a published contract.** Treat a
  change to it as breaking by default, and record the decision as an ADR in `docs/adr/`.

## 2. Implement

- Update the Contracts DTOs; keep the **`ApiResponse<T>`** envelope
  ([`../standards/api-response-format.md`](../standards/api-response-format.md)).
- **Never bind EF Core entities**; map DTO → command explicitly (mass-assignment safe).
- Version the route; mark a superseded version deprecated and emit `Deprecation` / `Sunset`
  headers.
- Add or adjust **FluentValidation**; return errors in `errors[]` with a stable `code`.
- Pick the status code deliberately from the table in
  [`../standards/api-response-format.md`](../standards/api-response-format.md) — including the
  hide-as-404, 409-for-optimistic-concurrency, and 410-vs-404 conventions.

## 3. Document (OpenAPI)

Document the endpoint, the request/response models, **every** error response and its `code`,
the auth requirement, and the version group
([`../standards/swagger-openapi.md`](../standards/swagger-openapi.md)). Refresh the committed
snapshot in `docs/api/` so the contract change shows up in the diff.

## 4. Sync frontend types

Run [`/sync`](../commands/sync.md) to regenerate `libs/data-access/api-types`. Remove any
now-duplicated hand-written type or client, and update callers to the versioned endpoint. A
type error after regeneration is the point of the exercise — fix the call site, never cast it
away.

## 5. Test

Integration-test the new or changed endpoint, including the validation path (400), the
authorization path (403/404), and the concurrency path (409) where applicable. Update the
architecture tests if a DTO shape guarantee changed.

## 6. Review & gate

[`/review`](../commands/review.md), then
[`pre-push-quality-gate.md`](pre-push-quality-gate.md). Confirm there is no silent contract
break and that deprecated versions still respond within their window. **No push without
approval.**
