# AjBoilerplate.Auth

The Auth service: a separate ASP.NET Core minimal-API host that signs users in and answers nginx's
`auth_request`. It is independent of the layered API: it references none of `Api`, `Application`,
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
| `POST /internal/admin/users/{subject}/refresh` | trusted callers, header `X-Admin-Api-Key`; 404 while `Auth:Admin:ApiKey` is empty |
| `GET /healthz`, `GET /readyz` | platform probes (liveness touches nothing; readiness pings Redis and SQL) |

## Configuration

Names only. Secrets come from the secrets provider and are never written into a file. The service refuses
to start when a required value is missing or invalid.

| Key | Notes |
|---|---|
| `ConnectionStrings:AuthDb`, `ConnectionStrings:Redis` | required |
| `Redis:CaCertPem` | optional private CA for Redis TLS |
| `Auth:PublicOrigin` | browser-facing origin, no path; builds the redirect URI `/auth/callback` |
| `Auth:LoggedOutPath` | default `/logged-out` |
| `Auth:Cookies:Secure` | default `true`; `false` drops the `__Host-` prefix and the Secure flag, Development only |
| `Auth:AllowInsecureUpstreams` | permits an http `Keycloak:TokenEndpointUrl` outside Development |
| `Auth:Entra:Authority`, `ClientId`, `ClientSecret`, `Scopes`, `RequestOwnClientIdScope`, `Timeout` | identity provider |
| `Auth:Keycloak:BaseUrl`, `Realm`, `SubjectIssuer`, `Scope`, `SendAudience`, `Timeout`, `MaxAttempts`, `RetryBaseDelay`, `TokenEndpointUrl` | token exchange |
| `Auth:Portal:ClientId`, `ClientSecret` | this app's Keycloak client |
| `Auth:Admin:ApiKey` | enables the admin endpoint |
| `Auth:Session:EncryptionKey` | base64 of 32 bytes |
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
