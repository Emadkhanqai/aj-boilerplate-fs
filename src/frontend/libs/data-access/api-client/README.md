# data-access/api-client

The only place in the workspace that talks HTTP.

| File | Purpose |
|---|---|
| `envelope-interceptor.ts` | Unwraps `ApiResponse<T>` so feature code sees plain `T`; turns `success: false` into a thrown `ApiError` whatever the HTTP status. |
| `auth-interceptor.ts` | Attaches the bearer token; refreshes once on 401 and retries, then reports session expiry. |
| `api-error.ts` | `ApiError`, plus `isConflictError` / `conflictData` / `apiErrorMessage` for the optimistic-concurrency (409) path. |
| `auth-token.ts`, `session-expiry.ts` | DI seams, so this library never imports `@aj-boilerplate/auth` (the module-boundary rule forbids it). They are bound in `apps/web/src/app/app.config.ts`. |
| `items-api.service.ts` | **SAMPLE** — the per-feature service pattern. Delete with `libs/feature-items`. |

## Adding an API service

One injectable per feature area, typed against `@aj-boilerplate/data-access/api-types`, no manual
envelope handling (the interceptor already unwrapped it), versioned paths only (`/api/v1/...`).
Copy `items-api.service.ts` and change the resource.

## Interceptor order

`authInterceptor` must run **before** `envelopeInterceptor` (see `app.config.ts`) so that by the
time it inspects a failure it is looking at the unwrapped `ApiError`, not a raw
`HttpErrorResponse`. Changing the order silently breaks refresh-on-401.
