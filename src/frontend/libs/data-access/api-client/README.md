# data-access/api-client

The only place in the workspace that talks HTTP.

| File | Purpose |
|---|---|
| `envelope-interceptor.ts` | Unwraps `ApiResponse<T>` so feature code sees plain `T`; turns `success: false` into a thrown `ApiError` whatever the HTTP status. |
| `auth-interceptor.ts` | Attaches **nothing**: the browser holds no token (nginx injects the JWT). On a 401 for a live gateway session it reports session expiry, which sends the user to `/login`; it stays quiet while the access-revoked dialog is open. |
| `access-revoked.ts` | Recognises `403` with `X-Usp-Access: revoked` and tells the shell to open the access-revoked dialog. Also tells a refused `/api/v1/me` apart from an ordinary 403. |
| `api-error.ts` | `ApiError`, plus `isConflictError` / `conflictData` / `apiErrorMessage` for the optimistic-concurrency (409) path. |
| `session-expiry.ts` | DI seams (`SESSION_EXPIRED_NOTIFIER`, `AMBIENT_SESSION_ACTIVE`), so this library never imports `@aj-boilerplate/auth` (the module-boundary rule forbids it). They are bound in `apps/web/src/app/auth-seams.ts`. |
| `items-api.service.ts` | **SAMPLE** — the per-feature service pattern. Delete with `libs/feature-items`. |
| `feature-announcements-api.service.ts` | "What's new" announcements: `unack(path)` / `ack(ids)`. Also declares `FeatureAnnouncement` **temporarily** — the endpoint is not in the OpenAPI document yet. Regenerate the types when it is, then delete the local interface. |

## Adding an API service

One injectable per feature area, typed against `@aj-boilerplate/data-access/api-types`, no manual
envelope handling (the interceptor already unwrapped it), versioned paths only (`/api/v1/...`).
Copy `items-api.service.ts` and change the resource.

## Interceptor order

`authInterceptor` must run **before** `envelopeInterceptor` (see `app.config.ts`) so that by the
time it inspects a failure it is looking at the unwrapped `ApiError`, not a raw
`HttpErrorResponse`. Changing the order silently breaks session-expiry handling on a 401.
