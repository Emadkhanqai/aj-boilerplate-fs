# auth

Session handling, route guards, and the role -> capability map.

## What lives here

| File | Purpose |
|---|---|
| `roles.ts` | The role list and the role -> capability map. **The only place** a role name may appear. |
| `auth.service.ts` | Owns the session signal, drives the active provider, exposes `capabilities()`. |
| `auth.guard.ts` | `authGuard` — "is there a session?" for the whole authenticated route group. |
| `capability.guard.ts` | `capabilityGuard('canEdit')` — per-route capability check. |
| `providers/dev-provider.ts` | Local role picker. No identity provider, synthetic tokens. |
| `providers/oidc-provider.ts` | Authorization Code + PKCE against any OIDC authority. |
| `sanitize-return-path.ts` | Open-redirect guard for the `?from=` post-login target. |

## Wiring a real identity provider

1. Set `window.__APP_AUTH_MODE__ = 'oidc'` and `window.__APP_OIDC_CONFIG__` in
   `apps/web/public/env.js`. In containers, `docker/40-env.sh` rewrites that file from
   environment variables at start-up — never commit real client ids or authority URLs.
2. Implement `GET /api/v1/me` in the backend, returning the `UserProfile` shape
   (`userId`, `displayName`, `email`, `roles`, `capabilities`). Once it is in the OpenAPI
   document, replace the hand-written `UserProfile` interface with the generated type.
3. Update `ROLES` and `Capabilities` in `roles.ts` to match what the server issues.

## The rule that matters

Everything here is **UX only**. Hiding a nav item or blocking a route does not protect anything —
the backend authorizes every request independently. Never implement a permission by hiding it in
the client.
