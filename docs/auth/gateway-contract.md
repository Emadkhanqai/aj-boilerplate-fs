# Gateway auth contract

This is the one contract shared by the four parts of sign-in: the **web nginx gateway**, the
**Auth service** (`AjBoilerplate.Auth`), the **API** (`AjBoilerplate.Api`) and the **Angular app**.
If you change anything here, change all four in the same PR.

It is a port of the Al Jazeera USP platform contract, as it ran on the USP staging branch in
November 2026. How to set it up is in [`setup.md`](setup.md); what to do when it breaks is in
[`troubleshooting.md`](troubleshooting.md). **Header names are kept identical to USP on purpose** (`X-Usp-*`). That way a
project can move between our own Auth service (`AUTH_PROVIDER=embedded`) and USP's shared
auth-backend (`AUTH_PROVIDER=usp`) without changing any portal code.

## The model in one paragraph

The browser never holds a token. It holds a single opaque, HttpOnly session cookie. Every
gated request goes through nginx. nginx sends `auth_request` to the Auth service's
`/auth/validate`, takes the Keycloak JWT from the answer, and forwards the request to the API as
`Authorization: Bearer <jwt>`. The API validates that JWT against Keycloak's JWKS like any other
bearer token. Sign-in is a server-side Authorization Code + PKCE flow against **Microsoft Entra
ID**. The Entra token is then exchanged at **Keycloak** (RFC 8693) for a JWT minted for this
portal's own Keycloak client, which carries the roles.

```
Browser ──cookie──▶ nginx (web container) ──auth_request──▶ Auth /auth/validate
                         │                                    │ 200 + X-Auth-Jwt
                         └──Authorization: Bearer <jwt>──▶ API (JwtBearer, Keycloak JWKS, azp check)
```

## Cookies (set and read only by the Auth service)

| Name | Purpose | Attributes |
|---|---|---|
| `__Host-app_sid` | Session id (opaque, 256-bit random) | Secure, HttpOnly, SameSite=Lax, Path=/ |
| `__Host-app_login` | Binds the in-flight login `state` to this browser | Secure, HttpOnly, SameSite=Lax, Path=/, 10 min |
| `__Host-app_noaccess` | One-shot marker set by a hand-off redeem that found no role; spent by the first `/auth/no-access` view. Carries no identity. | Secure, HttpOnly, SameSite=Lax, Path=/, 1 min |

Local http development uses the same names without the `__Host-` prefix (`app_sid`,
`app_login`, `app_noaccess`) and without the `Secure` flag, because browsers refuse `__Host-` on plain http.
This is controlled by `Auth:Cookies:Secure`, and `false` is refused outside Development.
The session cookie has no `Max-Age`: it is a browser-session cookie, and the server decides when
the session ends.

## Auth service endpoints

| Method + path | Who calls it | Result |
|---|---|---|
| `GET /auth/login?rd=<path>[&prompt=login\|select_account][&login_hint=<email>]` | browser (full-page) | 302 to the Entra authorize endpoint. `rd` is sanitised to a same-origin path. |
| `GET /auth/callback` | Entra redirect | Validates `state` (single use, bound to `__Host-app_login`), redeems the code (confidential client + PKCE), exchanges at Keycloak, creates the session, sets `__Host-app_sid`, then 302 to `rd`. A user with no role for this portal (or a token minted for a different client) gets a 403 "No access" page (the same page as `/auth/no-access`: it names the app and offers one "Back to sign-in" button). The identity provider returning `error=access_denied` gets a 403 "Access denied" page, any other provider error a 400 page. A Keycloak or Entra transient failure gets a 503 page. None of these redirects. |
| `GET /auth/validate` | **nginx only** (`internal`); browsers get 404 | See below. |
| `POST /auth/logout[?next=sign-in]` | browser, same-origin only (`Origin` or `Sec-Fetch-Site`) | Ends the session (reported to the portal as `logged-out`, see [session-ended-contract.md](session-ended-contract.md)), clears the cookie, then 303 to `/logged-out`. With `next=sign-in` it goes to `/auth/login?prompt=login` instead. The Entra SSO session is left alone. |
| `GET /auth/roles-version` | the SPA, every 15 s | `{"rolesVersion":"<16 hex>"}`, or `{"rolesVersion":null}` when the session has no roles. 401/403/503 mean the same as they do for validate. |
| `POST /auth/events` | the Pub/Sub push of Keycloak user changes (**not a browser surface**: nginx answers 404) | Disabled (404) unless `Auth:Events:Audience` and `Auth:Events:PushServiceAccount` are both set. Needs a Google ID token (`Authorization: Bearer`, signature always verified, `aud` = the audience, `email` = the push account): 401 otherwise. A message naming a Keycloak user (`sub`, `userId`, or `resourcePath` `users/<id>`) flags every session of that user `force_refresh`; anything else is acknowledged and ignored. Always 204 once authenticated, so Pub/Sub never redelivers an unparseable message. |
| `GET /auth/handoff?to=<client id>&rd=<path>` | browser, on the SOURCE portal | Cross-portal sign-in hand-off, see [handoff.md](handoff.md). Needs more than one portal (`Auth:Portals`); with one, any other `to` is a 404 page. `to` is a Keycloak client id from the configured portals, never a URL. 200 auto-submitting form page, or 303 to `rd` / to the target's `/auth/login`. |
| `POST /auth/handoff/redeem` | browser, on the TARGET portal | Spends the one-time ticket from the form body (needs `Origin` = the source portal's origin) and sets that portal's session cookie. Every outcome is a 303: to `rd`, to `/auth/login?rd=` (any refusal, never ends a session) or to `/auth/no-access` (a definitive no-role). `GET` on the same path redeems nothing and answers 303 `/`. |
| `GET /auth/no-access` | browser, after a hand-off redeem found no role | Renders the 403 "No access" page ONCE, naming the portal (`DisplayName`), with a button that posts `/auth/logout?next=sign-in`. The one-shot marker cookie (`__Host-app_noaccess`, 1 min) is spent by the view; without it the answer is 303 `/`. A gateway may set the marker and redirect here to serve the same page for a 403 it decided itself. |
| `POST /internal/admin/users/{subject}/refresh` | trusted callers | Flags every session of that subject `force_refresh`, so the next validate re-mints the JWT. Answers `{"flagged":<n>}`. |
| `GET /internal/admin/users?q=&limit=` | trusted callers | Searches the directory. `{"users":[{subject,username,email,firstName,lastName,enabled}]}`; a blank `q` answers an empty list. |
| `POST /internal/admin/users` | trusted callers | Grants a seat to a person by e-mail, creating the account if the realm has none. Body `{"email","firstName","lastName","groupPath"}`. 422 `EMAIL_DOMAIN_NOT_ALLOWED` unless the e-mail's domain is in `Auth:Admin:AllowedEmailDomains` (empty = every creation refused). Flags the person's sessions. |
| `GET /internal/admin/groups` | trusted callers | The managed paths and the seats under them, with a `portals` array saying which could be read. |
| `GET /internal/admin/members` | trusted callers | Everyone holding a seat, with those seats, and `portals`. A path that could not be read is UNKNOWN in `portals`, never an empty list. |
| `GET /internal/admin/users/{subject}/groups` | trusted callers | The seats the user holds under the managed paths. |
| `PUT /internal/admin/users/{subject}/groups` | trusted callers | Declarative: body `{"groups":[...]}` becomes the user's seats under the managed paths. Every path must be a seat under `Auth:Admin:ManagedGroupPaths` or nothing changes (400). Answers `groups`, `added`, `removed`, `portals`, `withheld`; flags the user's sessions when something moved. A failure half-way (503) still carries what actually landed. |
| `POST /internal/admin/users/{subject}/disable`, `.../enable` | trusted callers | Enables or disables the Keycloak account (204). Disabling ends the user's sessions at once, each reported as `signed-out-by-system` / `user_disabled`. |
| `/internal/admin/*` caller authentication | | **Every route** needs the header `X-Admin-Api-Key` (compared in constant time), or, when `Auth:Admin:Audience` and `AllowedCallers` are set, a signature-verified Google ID token from an allowed caller. Neither configured: the whole group is 404. 401 for a wrong or missing credential; 400 for a subject longer than 256 characters; 501 while no directory admin client is configured (`Auth:Admin:Keycloak`); 502 when the upstream refuses our credential or the request; 503 for an outage. No `/internal/` route is reachable through nginx. |
| `GET /healthz`, `GET /readyz` | platform probes | liveness and readiness |

### `/auth/validate` answers

| Status | Meaning | Response headers |
|---|---|---|
| 200 | Session valid | `X-Auth-Jwt: <keycloak jwt>`, `X-Usp-Session-Id: <stable per-session id, not the cookie>`, `X-Usp-Session-Started-At: <ISO-8601>`, and `X-Usp-Roles-Version: <16 hex>` when the JWT carries roles for this client. nginx does not forward the last one; the SPA reads the same value from `/auth/roles-version`. |
| 401 | No session, unknown session, idle or absolute lifetime exceeded, or Entra said `invalid_grant` | none |
| 403 | Definitive Keycloak verdict: this user has no role for this portal | `X-Usp-Access: revoked` (repeated for 15 minutes via a revoked marker) |
| 503 | Infrastructure fault (Keycloak, Entra, Redis or SQL unreachable, or timeout) and the current JWT has less than 5 s left | `X-Usp-Failure: auth` |

## Invariants (never break these)

1. **Only three things end a session:**
   - Entra `invalid_grant`, `interaction_required`, `consent_required` or `login_required`;
   - a definitive Keycloak "no role";
   - the **12 h absolute lifetime**, measured from sign-in and not extended by activity.
2. **An infrastructure fault never signs anyone out.** It serves the current JWT while it has
   more than 5 s left, otherwise answers 503.
3. **Only a 401 may become a login redirect.** A 403 or a 503 never does.
4. **`X-Usp-Access: revoked` is sent only on a definitive verdict**, never on a fault.
5. **Refresh is single-flight**, using a Redis lock. A rotated Entra refresh token is persisted
   even if the Keycloak exchange that follows fails.
6. **Never log** `Authorization`, `X-Auth-Jwt`, cookies, tokens, client secrets or claim
   payloads. Log reason slugs, status codes and route templates.
7. **Secrets come only from runtime configuration** (environment variables injected from Secret
   Manager or Key Vault by the platform). They are never written into a file. The API loads the
   secret store itself at boot; the Auth service does not, so its platform must inject them.

## Session lifetimes (defaults, all configurable under `Auth:Session`)

| Setting | Default |
|---|---|
| Idle timeout (sliding) | 8 h |
| Absolute lifetime | 12 h |
| Refresh skew | 2 min before JWT expiry |
| Refresh lock | 5 s, polled every 50 ms |
| Revoked marker | 15 min |
| Login state | 10 min |

Storage: SQL is the system of record (schema `auth`) and Redis is a cache in front of it. Rows
are keyed by SHA-256 of the cookie value. The JWT and the refresh token are encrypted with
AES-256-GCM using `Auth:Session:EncryptionKey`.

## nginx (web container) when `AUTH_PROVIDER=embedded`

- **Gated locations:** `location /` (the app shell) and `location /api/` both run
  `auth_request /_auth_validate`.
- **On `/api/`**, nginx sets these request headers. Each one **overwrites** anything the client
  sent:
  - `Authorization: Bearer $jwt`
  - `X-Usp-Session-Id`
  - `X-Usp-Session-Started-At`
  - `Cookie ""`
- **Ungated:** `/auth/*` (proxied to the Auth service, rate-limited, with `X-Auth-Jwt` hidden; `location = /auth/handoff` is its own block, see below),
  `= /login`, `= /logged-out`, static assets, `/env.js`, `/healthz`.
- **Browsers get 404** on `/auth/validate`, `/auth/events` and `/internal/`. The match tolerates
  repeated slashes and any letter case.
- **What each error status becomes:**

  | Status | Browser navigation | XHR |
  |---|---|---|
  | 401 | `302 /login?from=$request_uri` | the 401 is passed through |
  | 403 | a "No access" page, passing through `X-Usp-Access` | the 403 is passed through |
  | 5xx from validate | a "Service unavailable" page | 503 |

- **Response headers on every gateway response:** `Strict-Transport-Security` (sent on every
  response, including plain http, where browsers ignore it), `X-Content-Type-Options`,
  `X-Frame-Options`, `Referrer-Policy: same-origin`, `Permissions-Policy`, and a
  `Content-Security-Policy` whose `style-src` carries a per-request nonce (`$request_id`). The
  document gets the same nonce stamped into its markup.
- **`/auth/handoff` carries the Auth service's own CSP, as the only CSP.** The page it returns is built by the Auth service with a
  hash-allowed inline auto-submit script and no `form-action`, so that its form can POST to the **other** portal's
  `/auth/handoff/redeem`. The gateway CSP (`script-src 'self'`, `form-action 'self'`) would block both, and when two CSP headers are
  present the browser enforces both. So that one location includes `snippets/security-headers-no-csp.conf` (every header above except
  the CSP) instead of `security-headers.conf`. The other `/auth/` pages keep the gateway CSP beside the Auth service's own. Checked by
  `src/frontend/docker/check-gateway-config.sh`, which runs `nginx -t` for every profile and counts the CSP headers against a stub upstream.
- **Request id:** nginx sends its `$request_id` to the Auth service and API as `X-Request-Id` and
  prints it as "Reference" on its error pages, so a symptom in a browser joins a log line.
- **Buffer:** `proxy_buffer_size 8k`. The JWT header is larger than nginx's 4k default, and
  without this nginx answers 500.

## `AUTH_PROVIDER=usp`

The web container serves static files, answers 404 on `/api/` and `/auth/`, and does no gating. The project sits behind USP's shared
gateway nginx, which does all of the above against USP's auth-backend. To onboard, add:

- a portal entry in the USP auth-backend (`Auth:Portals:N` with `Host`, `ClientId` and
  `ClientSecret`);
- a Keycloak client for the portal;
- a host block in the USP gateway.

See `docs/auth/setup.md`.

## API side

- `Auth:Mode=gateway` is the only mode, and also the default when blank. Any other value stops the
  API at start-up. JwtBearer validates the issuer against `Keycloak:ValidIssuer`, with signing
  keys taken from `Keycloak:JwksUri` (pre-warmed at start-up).
  - Outside Development a plaintext `JwksUri` stops the API, unless
    `Keycloak:AllowInsecureJwksTransport=true` accepts it explicitly (a warning is then logged on
    every start). An empty `JwksUri` stops it too, unless `Keycloak:AllowDiscoveryFallback=true`.
  - **Audience is not validated.** `azp` must equal `Keycloak:ClientId` instead (fail closed).
  - `ClockSkew = 0`.
  - The fallback policy requires an authenticated user.
- **Roles:**
  - Seat roles come from `resource_access.<clientId>.roles` and the flat `roles` claim, and
    become `ClaimTypes.Role`. Realm roles are used only if `Keycloak:HonourRealmRoles=true`
    (local dev only).
  - Permissions shaped `resource:action` become the claim type
    `https://schemas.aj-boilerplate/claims/permission`.
  - A policy is satisfied by **a seat OR a permission**.
- **Shipped seats:** `admin`, `editor`, `viewer`. **Shipped example permissions:** `items:read`,
  `items:write`, `admin:manage`.
- **`GET /api/v1/me`** returns `userId`, `displayName`, `email`, `roles`, a `capabilities` object
  (`canView`, `canCreate`, `canEdit`, `canDelete`, `canAdminister`) and `permissions`. It needs only
  an authenticated caller, not a role: a signed-in user with no role gets a 200 with every
  capability false. The SPA learns the user only from this call.
- **Sign-ins are recorded** once per `X-Usp-Session-Id` in the `AuthenticationEvents` table by
  `SignInRecordingMiddleware`, best effort: a missing header or a database fault records nothing
  and never fails the request. **The end of a session** is written onto the same row by
  `POST /api/integration/auth-backend/session-ended`, a machine surface the Auth service calls straight on the API (never through the
  gateway) with `X-Auth-Events-Key`; see [session-ended-contract.md](session-ended-contract.md). `GET /api/v1/admin/authentication-history`
  lists the rows (administrators only).
- **Admin routes** `/api/v1/admin/{users,members,groups}` and `/api/v1/admin/users/{subject}/{groups,disable,enable,refresh}` are a
  proxy over the Auth service's `/internal/admin/*`, called with `X-Admin-Api-Key` (`AuthService:*`). They answer for administrators only
  (`admin:manage` or the `admin` seat). An upstream refusal is mapped to its own status and envelope code and never to a 403 about the
  administrator; the one Auth-service 404 that means "this person holds no seat" is told from a switched-off surface's bare 404 by its
  body text (see the comment on `AuthServiceIdentityAdministration.NoSeatMarker`).
- **`GET /api/v1/me/portals`** answers the configured `Portals` the caller's capabilities allow; the SPA's top-bar switcher reads it.
- **Rate limiting** runs after authentication and partitions on the user's subject.

## SPA side

- `AUTH_MODE=gateway` is the default for every deployed environment. `AUTH_MODE=dev` (the role
  picker) is for local UI work and e2e tests only.
- The SPA **holds no token**. Requests carry the cookie (same origin).
- `signIn()` does `location.assign('/auth/login?rd=…[&prompt=login][&login_hint=…]')`.
- `signOut()` POSTs `/auth/logout`, then goes to `/logged-out`. It also marks the browser so
  that the next sign-in sends `prompt=login`.
- **401** on a session that exists means the session expired, so redirect to `/login`. This is
  suppressed while the access-revoked dialog is open.
- **403 with `X-Usp-Access: revoked`** opens the access-revoked dialog. Its only action is to
  sign out.
- **The roles-version watcher:**
  - polls `/auth/roles-version` every 15 s while the tab is visible, and immediately after any
    403;
  - **on a change**, it re-reads `/api/v1/me`;
  - **fewer capabilities:** a blocking reload dialog;
  - **more capabilities:** a toast.
