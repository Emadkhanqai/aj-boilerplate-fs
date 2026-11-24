# AjBoilerplate.Auth

> New here? Read [`docs/auth/README.md`](../../../../docs/auth/README.md) first. This page is the
> service's own reference.

The Auth service: a separate ASP.NET Core minimal-API host that signs users in and answers nginx's
`auth_request`. Projects that run `AUTH_PROVIDER=usp` (inside the Al Jazeera USP platform) do **not**
deploy it: USP's shared auth-backend does this job. It is independent of the layered API: it references none of `Api`, `Application`,
`Domain`, `Infrastructure` or `Contracts`, and none of them reference it (the architecture tests enforce
both directions). The wire contract it implements is [`docs/auth/gateway-contract.md`](../../../../docs/auth/gateway-contract.md).

The browser never holds a token, only an opaque `HttpOnly` session cookie. Sign-in is a server-side
Authorization Code + PKCE (S256) flow against Entra ID (or Azure AD B2C, or any OIDC issuer under
`Auth:Entra:Authority`). The identity provider's access token is exchanged at Keycloak (RFC 8693) for a JWT
minted for this app's own client; nginx injects that JWT as `Authorization: Bearer` towards the API.

Sessions live in SQL Server (system of record, schema `auth`) with Redis in front. The key is the SHA-256 of
the cookie; the JWT and refresh token are encrypted with AES-256-GCM under `Auth:Session:EncryptionKey`.

## Endpoints

| Method + path | Who calls it |
|---|---|
| `GET /auth/login?rd=&prompt=&login_hint=` | browser |
| `GET /auth/callback` | identity provider redirect |
| `GET /auth/validate` | nginx only |
| `POST /auth/logout[?next=sign-in]` | browser, same origin only |
| `GET /auth/roles-version` | the SPA |
| `POST /auth/events` | the Pub/Sub push of Keycloak user changes; Google ID token; 404 until `Auth:Events:Audience` and `PushServiceAccount` are set |
| `GET /auth/handoff`, `POST /auth/handoff/redeem` | browser; the cross-portal sign-in hand-off ([`docs/auth/handoff.md`](../../../../docs/auth/handoff.md)), meaningful only with `Auth:Portals` |
| `GET /auth/no-access` | browser; the "No access" page, shown once after a hand-off redeem that found no role |
| `POST /internal/admin/users/{subject}/refresh`, `GET`/`POST /internal/admin/users`, `GET /internal/admin/groups`, `GET /internal/admin/members`, `GET`/`PUT /internal/admin/users/{subject}/groups`, `POST /internal/admin/users/{subject}/disable`, `.../enable` | trusted callers, **every route** with header `X-Admin-Api-Key` (or an allowed caller's Google ID token); 404 while neither is configured. The directory behind them is Keycloak's Admin REST API (`Auth:Admin:Keycloak`) |
| `GET /healthz`, `GET /readyz` | platform probes (liveness touches nothing; readiness pings Redis and SQL) |

`/auth/validate` answers 200 with `X-Auth-Jwt` (and the session headers), 401, 403 with
`X-Usp-Access: revoked`, or 503 with `X-Usp-Failure: auth`. The exact meanings are in the
[contract](../../../../docs/auth/gateway-contract.md).

## Run and test

```bash
docker compose up --build                       # from the repo root: the whole stack, Auth service included
dotnet test src/backend/tests/AjBoilerplate.Auth.Tests       # no Docker needed (fakes)
dotnet test src/backend/tests/AjBoilerplate.ArchitectureTests # includes the independence test
dotnet run --project src/backend/src/AjBoilerplate.Auth      # http://localhost:5090, needs SQL, Redis and the secrets below
```

Folders: `Configuration/` (options, the portal registry and start-up validation), `Endpoints/`, `Sessions/` (sign-in,
validation, refresh, force-refresh, hand-off, user administration, the session-ended dispatcher), `Identity/` (Entra, Keycloak and
Keycloak Admin clients, JWT claim reading), `Events/` (Google ID token verification, the Pub/Sub message), `SessionEvents/` (the
session-ended sender and its dispatch loop), `Security/` (token encryption, session ids, roles version), `Redis/`, `Sql/` (EF Core,
migrations, sweeper, the session-ended outbox), `Abstractions/`, `Observability/`.

Two things worth knowing before you read the code:

- **One portal, or several.** `Auth:Portal` is portal #0 and is all a single-portal project configures; nothing then reads a host.
  `Auth:Portals:N` adds portals on their own hosts that this one service signs in, and is what the hand-off needs.
- **Ending a session is a decision that is reported.** Every path that ends one goes through `ISessionStore.EndAsync` with a
  `SessionEnd` (reason and detail); in SQL that queues the session-ended event in the same transaction as the delete. A fault never
  ends a session, so it never queues one. See [`docs/auth/session-ended-contract.md`](../../../../docs/auth/session-ended-contract.md).

## Configuration

Names only; the full table with defaults and "required" is
[`docs/auth/setup.md`](../../../../docs/auth/setup.md#5-configuration-reference). **This service has no
secret-store loader**: it reads `appsettings.json` and environment variables. In a deployed
environment your platform injects the secrets (Secret Manager, Key Vault) as environment variables.
A secret is never written into a file. The service refuses to start when a required value is missing
or invalid.

| Key | Notes |
|---|---|
| `ConnectionStrings:AuthDb`, `ConnectionStrings:Redis` | required |
| `Redis:CaCertPem` | optional private CA for Redis TLS |
| `Auth:PublicOrigin` | browser-facing origin, no path; builds the redirect URI `/auth/callback` |
| `Auth:LoggedOutPath` | default `/logged-out` (portal #0's; `Auth:Portals:N:LoggedOutPath` overrides it per portal) |
| `Auth:ForwardedHostHeader` | default `X-Forwarded-Host`; read only when `Auth:Portals` is non-empty, to choose the portal |
| `Auth:Cookies:Secure` | default `true`; `false` drops the `__Host-` prefix and the Secure flag, Development only |
| `Auth:AllowInsecureUpstreams` | permits http for the Entra authority and endpoint URLs and for the Keycloak base and token URLs outside Development |
| `Auth:Entra:Authority`, `ClientId`, `ClientSecret`, `Scopes`, `RequestOwnClientIdScope`, `Timeout` | identity provider. Defaults are USP staging's Entra ID values: authority `https://login.microsoftonline.com/<tenant-id>`, scopes `openid profile offline_access User.Read` (a Graph scope, so the access token is a Graph token and Keycloak's `oidc` provider can validate it), `RequestOwnClientIdScope=false`. For Azure AD B2C set scopes `openid profile offline_access`, `RequestOwnClientIdScope=true` and `Keycloak:SubjectIssuer` to the B2C alias |
| `Auth:Entra:AuthorizeEndpointUrl`, `TokenEndpointUrl` | optional full URLs for an issuer whose endpoints are not `{Authority}/oauth2/v2.0/*` (the local Keycloak stand-in); the first is where the browser goes, the second is POSTed by the service |
| `Auth:Keycloak:BaseUrl`, `Realm`, `SubjectIssuer` (default `oidc`; empty omits the parameter and is refused outside Development), `Scope`, `SendAudience`, `Timeout`, `MaxAttempts`, `RetryBaseDelay`, `TokenEndpointUrl` | token exchange |
| `Auth:Portal:ClientId`, `ClientSecret`, `DisplayName` | this app's Keycloak client (portal #0), and the name the "No access" page uses |
| `Auth:Portal:SessionEvents:Url`, `Audience`, `Key` | where portal #0 is told its sessions ended; the call carries `X-Auth-Events-Key` when `Key` is set, else a Google ID token. Nothing is queued without `Url` |
| `Auth:Portals:N:Host`, `ClientId`, `ClientSecret`, `LoggedOutPath`, `DisplayName`, `SessionEvents:Url`, `Audience`, `Key` | OPTIONAL further portals (hand-off targets); `Host` is a bare DNS host, unique |
| `Auth:SessionEndDelivery:PollInterval`, `BatchSize`, `ClaimLease`, `Timeout`, `InitialRetryDelay`, `MaxRetryDelay`, `MaxAge`, `Retention` | outbox delivery tuning; the defaults are the contract's (48 h, 15 s doubling to 15 min) |
| `Auth:Events:Audience`, `Auth:Events:PushServiceAccount` | enable `POST /auth/events`; both or neither |
| `Auth:Admin:ApiKey` | enables the admin endpoints (`X-Admin-Api-Key`) |
| `Auth:Admin:Audience`, `Auth:Admin:AllowedCallers` | OPTIONAL: also accept a signature-verified Google ID token from these callers; both or neither |
| `Auth:Admin:ManagedGroupPaths`, `Auth:Admin:AllowedEmailDomains` | the Keycloak group paths the admin surface may touch (a seat is a group UNDER one), and the e-mail domains a new account may be created for (empty refuses every creation) |
| `Auth:Admin:Keycloak:ClientId`, `ClientSecret`, `BaseUrl`, `Realm`, `Timeout` | the dedicated Keycloak client (service account, client-credentials) behind the directory routes; `BaseUrl` and `Realm` default to `Auth:Keycloak`'s. Without it the directory routes answer 501 |
| `Auth:Session:EncryptionKey` | base64 of 32 bytes |
| `Auth:Session:HandoffTicketLifetime` | how long a hand-off ticket may wait to be redeemed (60 s) |
| `Auth:Session:IdleTimeout`, `AbsoluteLifetime`, `RefreshSkew`, `RevokedMarkerLifetime`, `LoginStateLifetime`, `RefreshLockLifetime`, `LockPollInterval`, `LockExpiryGrace`, `MinimumServableLifetime`, `SweepInterval` | lifetimes and tuning |

Locally, `Properties/launchSettings.json` sets the non-secret Development values; supply the secrets as
environment variables or user-secrets (for example `Auth__Entra__ClientSecret`).

## Migrations

Schema changes are EF Core migrations in `Sql/Migrations`, applied as a controlled release step, never at
start-up and never with `EnsureCreated`. The history table is `auth.__EFMigrationsHistory`.

```bash
dotnet ef migrations add <Name> --project src/backend/src/AjBoilerplate.Auth --startup-project src/backend/src/AjBoilerplate.Auth --output-dir Sql/Migrations
AUTH_DB_CONNECTION='<connection string>' dotnet ef database update --project src/backend/src/AjBoilerplate.Auth --startup-project src/backend/src/AjBoilerplate.Auth
```

## Image

```bash
docker build -f src/backend/src/AjBoilerplate.Auth/Dockerfile src/backend
```

The `build` stage carries `dotnet-ef` for the migrate job; the `runtime` stage does not.
