---
description: Sync the frontend API layer to the backend OpenAPI — regenerate types, remove duplicated DTOs, and confirm versioned-endpoint usage. Does not push.
---

# /sync

Keep the frontend contract in lockstep with the backend OpenAPI document. Run this after **any**
change to the API surface — the wider procedure is
[`../workflows/api-change.md`](../workflows/api-change.md).

## Do this

1. Ensure the backend builds and the OpenAPI document is current
   (`/swagger/v{version}/swagger.json`) — see
   [`../standards/swagger-openapi.md`](../standards/swagger-openapi.md).
2. Refresh the committed OpenAPI snapshot in `docs/api/` so the contract change is visible in
   the diff.
3. Regenerate the frontend types into `libs/data-access/api-types`:
   ```bash
   cd src/frontend
   npm run generate:api        # openapi-typescript against the backend document
   ```
4. **Remove any hand-written type or client that the generated output now covers.** Hand-written
   HTTP clients are prohibited; a hand-copied DTO is a contract drift waiting to happen.
5. Confirm every call site uses a **versioned** endpoint (`/api/v1/...`) and unwraps the
   `ApiResponse<T>` envelope **centrally**, surfacing `traceId` on errors.
6. Prove it compiles:
   ```bash
   npx nx run-many -t typecheck lint build
   ```

## Rules

- Generated files are source-controlled but **never hand-edited** — an edit is destroyed on the
  next regeneration.
- If a breaking change forced a new API version, update the callers to the new version
  **deliberately**. Do not silently follow a moved contract.
- A type error after regeneration is the point of this exercise. Fix the call site; do not cast
  it away.

## Output

List the regenerated files, any hand-written types or clients removed as now-duplicated, and
every call site updated. **Do not push** — leave the result for review and approval.
