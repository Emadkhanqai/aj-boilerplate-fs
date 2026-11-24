# auth

Session handling, route guards, and the role -> capability map.

The browser **holds no token**. In every deployed environment the session is an opaque HttpOnly
cookie owned by the Auth service and checked by nginx on every request; this library only drives the
sign-in/sign-out navigations and learns who the user is from `GET /api/v1/me`. The contract all four
parts share (nginx, Auth service, API, this app) is `docs/auth/gateway-contract.md` at the repo root.

## What lives here

| File | Purpose |
|---|---|
| `roles.ts` | The role list, the role -> capability map and the `permissions` -> capability map. **The only place** a role or permission name may appear. |
| `auth.service.ts` | Owns the session signal, drives the active provider, exposes `capabilities()`. |
| `auth.guard.ts` | `authGuard` — "is there a session?" for the whole authenticated route group. |
| `capability.guard.ts` | `capabilityGuard('canEdit')` — per-route capability check. Three outcomes: login, "access check unavailable" (the server did not answer), "access denied" (the server said no). |
| `providers/gateway-provider.ts` | **Default for deployed builds.** `signIn()` = full-page navigation to `/auth/login`; `signOut()` = `POST /auth/logout`, then `/logged-out`. |
| `providers/dev-provider.ts` | Local role picker. No identity provider. Local UI work and e2e only. |
| `gateway-signed-out-marker.ts` | "This browser signed out", so the next sign-in sends `prompt=login`. |
| `gateway-auto-sign-in.ts` | The one-per-five-minutes brake that stops `/login` auto-starting a redirect loop. |
| `login-hint.ts` | Validates the optional email passed to the identity provider as `login_hint`. |
| `role-change.ts` | Classifies a change in capabilities as reduction / increase / unchanged. The watcher that polls `/auth/roles-version` lives in `libs/shell/src/lib/role-change/`. |
| `sanitize-return-path.ts` | Open-redirect guard for the `?from=` post-login target. |

## Runtime configuration

`window.__APP_AUTH_MODE__` (`apps/web/public/env.js`, rewritten at container start by
`docker/40-env.sh` from `AUTH_MODE`):

- `gateway` — the default for any value that is not exactly `dev`;
- `dev` — the role picker. It must be asked for by name.

`AUTH_PROVIDER` (`embedded` | `usp`) is read by nginx, not by the browser. See
`docker/16-nginx-profile.sh` and `docker/nginx/`.

## What `/api/v1/me` must return

```json
{ "userId": "…", "displayName": "…", "email": "…", "roles": ["Editor"],
  "capabilities": { "canView": true, "canCreate": true, "canEdit": true, "canDelete": false, "canAdminister": false },
  "permissions": ["items:read", "items:write"] }
```

`permissions` is optional. The UI gates on the union of the server's `capabilities`, what the roles
grant and what the permissions grant (`capabilitiesForProfile`), because the API admits a caller by
a seat **or** a permission. `UserProfile` is hand-written, because the generated `UserProfileResponse`
makes every field optional. `user-profile.contract.spec.ts` stops it drifting: the committed
`docs/api/openapi.json` must carry every field `UserProfile` names.

A signed-in user with no role still gets a 200 with every capability false; that is how the SPA
tells "no role" from "the endpoint is broken". When the call fails for any other reason, the
`capabilityGuard` sends the user to an "access check unavailable" page rather than "access denied",
because an outage is not a verdict.

## The rule that matters

Everything here is **UX only**. Hiding a nav item or blocking a route does not protect anything —
the backend authorizes every request independently. Never implement a permission by hiding it in
the client.
