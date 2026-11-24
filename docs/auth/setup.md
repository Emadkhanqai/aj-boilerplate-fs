# Auth setup

How to configure sign-in, from "it works on my laptop" to a real Microsoft Entra ID tenant.
For how the pieces fit together, read the [overview](README.md) first. For the exact wire
behaviour, read the [gateway contract](gateway-contract.md). If something is broken, go to
[troubleshooting](troubleshooting.md).

> **What this file can and cannot promise.** Everything under "Local development" is run by
> `docker compose up` and covered by tests in this repository. Everything under "Real Entra ID"
> is **not exercised here**: no test and no compose service in this repository contacts a real
> Entra tenant or a Keycloak with a real Entra identity provider. The default values equal
> the ones the Al Jazeera USP platform ran against Entra ID on its staging environment, and the
> reasons for them are in the code comments of `AuthOptions.cs`. Treat the Entra and Keycloak
> steps as a checklist to verify, not as a recipe that has been proven here. `infra/gcp` deploys the
> Auth service and the web gateway but never Keycloak, and `infra/azure` deploys neither (see
> [Deploying](#deploying-the-auth-service-and-the-web-gateway) and [What is not deployed](#what-is-not-deployed)).

## Pick your path

| You are... | `AUTH_PROVIDER` | Auth service | Read |
|---|---|---|---|
| Developing locally | `embedded` | runs in compose | [1. Local development](#1-local-development) |
| A project outside USP, with your own Entra tenant and Keycloak | `embedded` | you deploy it | [2. Real Entra ID](#2-real-entra-id) |
| A project inside the Al Jazeera USP platform | `usp` | **not deployed**; USP's shared auth-backend does the job | [3. Onboarding into USP](#3-onboarding-into-usp-auth_providerusp) |

---

## 1. Local development

One command starts everything, with no Microsoft tenant:

```bash
cp .env.example .env     # set MSSQL_SA_PASSWORD
docker compose up --build
```

Open <http://localhost:4200>. You are sent to `/login`, then to the bundled Keycloak, then back
signed in.

### What the bundled Keycloak is doing

One Keycloak container plays **both** identity roles that exist in a real deployment. Its realm
is [`infra/keycloak/realm-aj-boilerplate.json`](../../infra/keycloak/realm-aj-boilerplate.json),
imported at start-up. Every secret in it is a committed, dev-only value.

| Realm client | Plays | Used by |
|---|---|---|
| `aj-boilerplate-entra-sim` | **Entra ID**: the browser signs in here, and the Auth service redeems the code against it | Auth service, `Auth__Entra__*` |
| `aj-boilerplate-app` | **The portal's Keycloak client**: the Auth service exchanges the sign-in token for a JWT minted for this client. The API checks `azp` equals it. Its client roles are the seats and permissions. | Auth service `Auth__Portal__*`, API `Keycloak__ClientId` |
| `aj-boilerplate-admin` | **The directory admin client**: a dedicated client with a service account holding the `realm-management` roles `view-users`, `query-users`, `query-groups` and `manage-users`. The Auth service's `/internal/admin/*` calls Keycloak's Admin REST API as it, which is what the Users & roles page ends up using. Not the app client. It keeps Keycloak's default full scope: with `fullScopeAllowed` off its token carries no `realm-management` roles and every admin call is answered 403. | Auth service `Auth__Admin__Keycloak__*` |

**Seats are held through groups**, the way a deployed realm does it. The realm has the group `/app` and, under it, `/app/admins`,
`/app/editors` and `/app/viewers`; each carries the app client's seat role of the same name (`admin`, `editor`, `viewer`), and each
seat role is a composite of its permissions. A user is in a seat by being a member of the group. The Auth service's
`Auth__Admin__ManagedGroupPaths__0` is `/app`, so the admin surface reads and writes only groups under it.

Because both live in one realm, the token exchange is Keycloak's standard (internal-to-internal)
one, enabled by `KC_FEATURES=token-exchange-standard` in `docker-compose.yml`. That is why the
compose file sets `Auth__Keycloak__SubjectIssuer` to an **empty string**: there is no `oidc`
identity provider to name. An empty `SubjectIssuer` is refused by the Auth service outside the
Development environment.

### Seeded users

All five share one password, `LOCAL_USER_PASSWORD`, which defaults to the committed dev-only
value `dev-only-password-never-reuse` (see `docker-compose.yml`). Override it in `.env` if you
like. These accounts exist only in the local container.

| Username | Member of | What you should see |
|---|---|---|
| `admin` | `/app/admins` | Everything, including **New Item**, delete, **Users & roles** and **Sign-in history** (under Administration) |
| `editor` | `/app/editors` | Create and edit items; no delete; no Administration |
| `editor2` | `/app/editors` | A second editor, so the member list has more than one person to search and re-seat |
| `viewer` | `/app/viewers` | Read-only |
| `noaccess` | no group | Signs in at Keycloak, then the Auth service shows a **403 "No access"** page. This is the "authenticated but no role" case. |

### What else the local stack wires up

Everything below is in `docker-compose.yml` with DEV-ONLY values, so the admin screens work with one `docker compose up`:

| Feature | How it is wired locally |
|---|---|
| Users & roles | API `AuthService__BaseUrl=http://auth:8080` and `AuthService__AdminApiKey` equal to the Auth service's `Auth__Admin__ApiKey` (`LOCAL_AUTH_ADMIN_API_KEY`). The Auth service reaches Keycloak with the `aj-boilerplate-admin` client (`LOCAL_ADMIN_CLIENT_SECRET`). Creating a person is allowed only for `Auth__Admin__AllowedEmailDomains__0=example.com` (the seeded users are `example.test`, so they cannot be created twice). |
| Sign-in history shows how a session ended | `Auth__Portal__SessionEvents__Url` points at the API's receiver and `Auth__Portal__SessionEvents__Key` equals the API's `Integration__AuthBackend__EventsKey` (`LOCAL_AUTH_EVENTS_KEY`). See [session-ended-contract.md](session-ended-contract.md). A logout reaches the history within the outbox poll interval (`Auth:SessionEndDelivery:PollInterval`, 5 s). |
| Portal switcher | API `Portals__0__*` and `Portals__1__*`: two sample entries. The second only demonstrates the menu; it opens the same app. |
| `/auth/events` (the Pub/Sub push of Keycloak user changes) | **Not wired locally**: it stays off (404) until `Auth__Events__Audience` and `Auth__Events__PushServiceAccount` are set, which needs a Google Pub/Sub push. |
| `/auth/handoff` between portals | Needs a second portal in `Auth:Portals`; not configured locally, so `/auth/handoff?to=<anything else>` is the designed 404 page. See [handoff.md](handoff.md). |

### Ports and URLs

| Service | URL | Notes |
|---|---|---|
| Web (nginx gateway) | <http://localhost:4200> (`WEB_PORT`) | the only thing a browser should use |
| API | <http://localhost:8080> | published for tooling; **a browser call here has no session**, so protected endpoints answer 401 |
| Keycloak | <http://localhost:8081> | admin console, user `admin`, password `LOCAL_KEYCLOAK_ADMIN_PASSWORD` (committed dev default) |
| Auth service | not published | reachable only through the web container's `/auth/*` |

Local http cookies drop the `__Host-` prefix and the `Secure` flag (`Auth__Cookies__Secure=false`),
because browsers refuse `__Host-` on plain http. That setting is refused by the Auth service
outside Development.

### UI work without a backend

| Command | Result |
|---|---|
| `cd src/frontend && npm run dev:offline` | MSW mocks the whole API in the browser. A role picker replaces sign-in. No Docker needed. |
| `AUTH_MODE=dev docker compose up --build web` | The container serves the role picker and proxies `/api/` **ungated**. The API runs in gateway mode and expects a JWT, so every real API call answers 401. UI-only. |

`AUTH_MODE=dev` is for demos and UI work. It is not a way to test authorisation.

### Running a service without Docker

| Service | How |
|---|---|
| API | `dotnet run --project src/backend/src/AjBoilerplate.Api`. Without `Keycloak__*` set, **every protected endpoint answers 401**, which is by design. Set `Keycloak__ValidIssuer` and `Keycloak__JwksUri` to a Keycloak you run yourself. |
| Auth service | `dotnet run --project src/backend/src/AjBoilerplate.Auth`. `Properties/launchSettings.json` sets non-secret Development values (it points at placeholder Entra and Keycloak hosts, so you must override them). Secrets come from environment variables or user-secrets. |

Run the Auth service's migrations first (see the
[Auth service README](../../src/backend/src/AjBoilerplate.Auth/README.md#migrations)).

---

## 2. Real Entra ID

You need three things configured, in this order: Entra, Keycloak, then the Auth service.

### 2a. Entra ID app registration

Create one app registration for the Auth service (Microsoft Entra admin centre, *App
registrations*).

| Setting | Value | Why |
|---|---|---|
| Platform | **Web** (not "Single-page application") | The Auth service is a server-side confidential client. It redeems the code with a **client secret** and PKCE. A SPA registration forbids a client secret and changes how redirects work. |
| Redirect URI | `<PublicOrigin>/auth/callback`, for example `https://app.example.com/auth/callback` | Must equal `Auth__PublicOrigin` plus `/auth/callback`, character for character. |
| Client secret | create one; copy the **value** once | Becomes `Auth__Entra__ClientSecret`. Store it in your secrets provider. |
| API permissions | Microsoft Graph, **delegated**, `User.Read` | This is what makes the access token a Graph token. See below. |
| Supported account types | your tenant only (single tenant) unless you know you need more | The authority is `https://login.microsoftonline.com/<tenant-id>`. |

Note down the **Directory (tenant) ID** and **Application (client) ID**. They are not secrets, but
they are yours, so they never go in this repository: use placeholders in any file you commit.

### Why the scopes are what they are

The defaults in `AuthOptions.cs` are:

| Setting | Default | Reason |
|---|---|---|
| `Auth:Entra:Scopes` | `openid profile offline_access User.Read` | `offline_access` yields the refresh token the session lives on (the service refuses to start without it). `User.Read` makes the access token a **Microsoft Graph** token. |
| `Auth:Entra:RequestOwnClientIdScope` | `false` | Adding the app's own client id as a scope made Entra answer `AADSTS650053` ("scope does not exist on the resource"). That is an Azure AD B2C idiom, not an Entra one. |

The Keycloak `oidc` identity provider validates the Entra token by calling Graph's userinfo
endpoint, and Graph accepts only a token whose audience is Graph. A `<client-id>/.default` scope
was tried by the USP team and produced a token with the app itself as audience, so the Keycloak
exchange failed. The source of this explanation is the comment on `EntraOptions` in
`src/backend/src/AjBoilerplate.Auth/Configuration/AuthOptions.cs`.

**Azure AD B2C** still works by configuration: set `Auth__Entra__Authority` to the policy URL,
`Auth__Entra__Scopes` to `openid profile offline_access`, `Auth__Entra__RequestOwnClientIdScope`
to `true`, and `Auth__Keycloak__SubjectIssuer` to the alias of the Keycloak provider that trusts
B2C. This repository does not exercise it.

### 2b. Keycloak

You need a Keycloak realm with these pieces. The names below are the ones the local realm uses;
yours can differ as long as the configuration matches.

1. **The portal's confidential client** (for example `<your-api-client-id>`):
   - confidential (client authentication on), standard flow **off**, no direct access grants;
   - **token exchange enabled** for it (this boilerplate's local realm sets the client attribute
     `standard.token.exchange.enabled`);
   - the client secret becomes `Auth__Portal__ClientSecret`.
2. **Client roles** on that client: the seats `admin`, `editor`, `viewer` and, if you use them,
   the permissions `items:read`, `items:write`, `admin:manage`. Names are matched
   case-insensitively; anything else is dropped (see [4. Roles and permissions](#4-roles-and-permissions-end-to-end)).
3. **Two ways the roles reach the token**, both of which the API reads:
   - the realm's default `roles` client scope, which writes `resource_access.<clientId>.roles`;
   - a **flat `roles` claim**: a mapper of type *User Client Role* on this client, pointed at
     this client, claim name `roles`, multivalued string, added to the access token.
4. **An identity provider that trusts Entra**, with alias `oidc` (or whatever you set in
   `Auth__Keycloak__SubjectIssuer`). This is the `subject_issuer` of the RFC 8693 exchange. The
   exchange sends `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`, the Entra
   **access** token as `subject_token`, `subject_issuer=<alias>` and the portal client's
   credentials. Keycloak validates the subject token against the Entra provider and returns a
   token for the portal client.
5. **Users in Keycloak** are created or linked by that identity provider; assign them client
   roles on the portal client. A user with no role gets a definitive "no role" answer and the
   Auth service shows the 403 page.

> **Not exercised in this repository.** Exchanging an *external* token (the `subject_issuer`
> form) is a different Keycloak feature from the *internal* exchange the local stack uses. Which
> Keycloak feature flags, client permissions and version it needs is a Keycloak question, so
> check your Keycloak version's token-exchange documentation. Do not assume the local realm's
> settings carry over.

### 2c. The Auth service

Set these (full list in [the reference](#5-configuration-reference)). **Names only** are shown
for secrets.

| Key | Value / source |
|---|---|
| `Auth__PublicOrigin` | `https://<your-host>`; no path, no trailing slash |
| `Auth__Entra__Authority` | `https://login.microsoftonline.com/<tenant-id>` |
| `Auth__Entra__ClientId` | the app registration's client id |
| `Auth__Entra__ClientSecret` | **secret** |
| `Auth__Keycloak__BaseUrl`, `Auth__Keycloak__Realm` | your Keycloak |
| `Auth__Keycloak__SubjectIssuer` | the identity provider alias, default `oidc` |
| `Auth__Portal__ClientId`, `Auth__Portal__ClientSecret` | the portal client; the secret is a **secret** |
| `Auth__Session__EncryptionKey` | **secret**: `openssl rand -base64 32` |
| `Auth__Admin__ApiKey` | **secret**, optional; empty disables the admin endpoint |
| `ConnectionStrings__AuthDb`, `ConnectionStrings__Redis` | **secret** when they carry a password |

On the API, set `Keycloak__ValidIssuer`, `Keycloak__JwksUri` (https), and `Keycloak__ClientId` to
the **same** portal client id as `Auth__Portal__ClientId`. A mismatch is the most common reason
for a 401 loop (see [troubleshooting](troubleshooting.md)).

### Secrets: how they reach the Auth service

The API loads secrets from Secret Manager (`CLOUD_PROVIDER=gcp`) or Key Vault (`azure`) at boot
through `AddCloudSecrets()`. **The Auth service has no such loader.** It reads ordinary .NET
configuration: `appsettings.json`, then environment variables. So the platform has to inject the
secrets as environment variables (Cloud Run secret references, Container Apps Key Vault
references, or your own mechanism). Never put a value in a file.

Secret **names** to provision, for both clouds:

| Secret | Environment variable it feeds |
|---|---|
| Entra client secret | `Auth__Entra__ClientSecret` |
| Keycloak portal client secret | `Auth__Portal__ClientSecret` |
| Session encryption key | `Auth__Session__EncryptionKey` |
| Admin API key (optional) | `Auth__Admin__ApiKey` |
| Auth database connection string | `ConnectionStrings__AuthDb` |
| Redis connection string | `ConnectionStrings__Redis` |
| App database connection string (API) | `ConnectionStrings__Default` |
| Entity-id obfuscation key (API) | `EntityIdObfuscation__EncryptionKey` |

### Run the migration, then verify

The Auth service never migrates at start-up. Apply its EF Core migration as a release step, as
the compose file does with `auth-migrate`, or with the command in the
[Auth service README](../../src/backend/src/AjBoilerplate.Auth/README.md#migrations).

A deployment is verified when:

1. `GET /healthz` is 200 on the Auth service and `GET /readyz` is 200 (it pings Redis and SQL).
2. The Auth service **starts**. A missing or invalid value stops it with a message naming the
   key; read the log.
3. Opening the site redirects to Microsoft, and you return to `/auth/callback` and then the app.
4. `GET /api/v1/me` in the browser returns your roles and permissions.
5. A user with no role gets the 403 "No access" page, not a login loop.

### Deploying the Auth service and the web gateway

**On Google Cloud only**, and not verified by an apply: `infra/gcp` (Terraform) deploys the web
container, the Auth service and its migration job, alongside the API, and `.github/workflows/deploy.yml`
builds the two images and rolls the four out in a safe order. Start at
[`infra/gcp/README.md`](../../infra/gcp/README.md); this section is the sign-in half of it.

**Keycloak is external.** An existing Keycloak server is used, as on USP. `infra/gcp` takes its base
URL, realm, issuer, JWKS URL and client id as variables and reads the portal client's secret from
Secret Manager. It does not deploy, configure or change Keycloak or the identity provider, so
everything in sections 2a and 2b is done by hand first.

What you create before the first deployment:

| What | Where it goes |
|---|---|
| Identity provider app registration (section 2a), redirect URI **`<public origin>/auth/callback`** | `entra_authority`, `entra_client_id` |
| Keycloak client, roles and identity provider (section 2b) | `keycloak_base_url`, `keycloak_realm`, `keycloak_portal_client_id`, `keycloak_subject_issuer` |
| Secret Manager secret holding the app registration's client secret | name `<name_prefix>-<environment>-entra-client-secret`, becomes `Auth__Entra__ClientSecret` |
| Secret Manager secret holding the portal client's secret | name `<name_prefix>-<environment>-keycloak-client-secret`, becomes `Auth__Portal__ClientSecret` |

The public origin is not known until the web service exists, unless you set `public_origin` (a custom
domain or a load balancer). `terraform output web_url` prints it. Register the redirect URI, then sign in.

What the infrastructure generates and injects by Secret Manager reference, so you do not: the Auth
database connection string, the session encryption key, the Redis address and, only when
`auth_admin_api_key_enabled` is true, the admin API key.

The web container runs with `UPSTREAM_AUTH=cloud-run-id-token`: the API and the Auth service accept
only a caller holding a Google ID token for the gateway's service account, and the gateway sends one
on every hop as `X-Serverless-Authorization`. Why that and not `ingress = internal`, and what it costs,
is in the infra README. Locally and anywhere else the default `none` applies and nothing changes.

The Auth service's migrations are applied by a Cloud Run job, `auth-migrate`, that the deploy executes
and waits for before it rolls out the Auth service. It is a release step, never start-up.

**Not verified:** no apply, no image build against a registry and no sign-in through a deployed gateway
has been run. Verify a first deployment with the five checks above ("Run the migration, then verify").

### What is not deployed

- **On Azure, nothing of this.** `infra/azure` (Bicep) provisions the API, its database, Redis and
  secrets only, and `deploy.yml` rolls out only the API image there. Deploying the web container and
  the Auth service on Azure is your project's work; the infrastructure is Google Cloud only.
- **On either cloud, Keycloak and the identity provider.** They are existing external services.
- **Behind a load balancer**, remember that nginx must receive `X-Forwarded-Proto` (Cloud Run sends it),
  and that the web container's rate limits also key on the peer address, so configure
  `set_real_ip_from` for your balancer; see [troubleshooting](troubleshooting.md).

---

## 3. Onboarding into USP (`AUTH_PROVIDER=usp`)

Inside the Al Jazeera USP platform, a shared gateway nginx and a shared auth-backend do the
gating. Your project **does not deploy the Auth service**. Set `AUTH_PROVIDER=usp` on the web
container: it then serves static files only, answers 404 on `/api/` and `/auth/`, and does no
gating.

What the USP platform team has to add for your portal. **Ask the USP platform team** for the
current form of each; this repository does not know what their files contain today.

- [ ] A **portal entry in the USP auth-backend** for your host: host name, your Keycloak client id,
      and its client secret.
- [ ] A **Keycloak client** for your portal, with the seats and permissions as client roles and
      the flat `roles` claim mapper (same shape as section 2b).
- [ ] A **host block in the USP gateway** that routes `/api/` to your API, `/auth/` to the
      auth-backend, and everything else to your web container.

What stays your job:

- [ ] Set the API's `Keycloak__ValidIssuer`, `Keycloak__JwksUri` and `Keycloak__ClientId` to the
      values for your client.
- [ ] If USP's Keycloak serves its JWKS only over internal http, the API refuses to start outside
      Development until you set `Keycloak__AllowInsecureJwksTransport=true`. Do that only with a
      written, dated reason; the API then logs a warning on every start.
- [ ] Keep the role names in this repository aligned with the client roles USP issues.
- [ ] Confirm the gateway sends `X-Usp-Session-Id` and `X-Usp-Session-Started-At` to the API, or
      sign-ins are not recorded (nothing else breaks).

---

## 4. Roles and permissions, end to end

Seats (`admin` > `editor` > `viewer`) are a hierarchy. Permissions (`items:read`,
`items:write`, `admin:manage`) are separate grants. A policy is satisfied by **a seat OR a
permission**. The same function, `RoleCapabilities.For(roles, permissions)`, decides both the
server's answer and the flags `GET /api/v1/me` returns, so the two cannot drift.

### Add a new permission

Example: `reports:export`.

1. **Keycloak**: add a client role named `reports:export` on the portal client; assign it.
2. **API vocabulary**: add the constant to `Permissions` and to `Permissions.All`
   (`src/backend/src/AjBoilerplate.Application/Identity/Permissions.cs`). Anything not in
   `All` is dropped and logged once as an unrecognised key. This is deliberate: nothing is
   granted implicitly.
3. **API policy**: add a policy that uses it, in `AuthenticationSetup.AddAuthorizationPolicies`,
   with `RequireSeatOrPermission(Permissions.ReportsExport, ...seats)`, and a name in `Policies`.
4. **API endpoint**: `[Authorize(Policy = Policies.ExportAccess)]`. Every controller must
   declare a posture; an architecture test fails otherwise.
5. **Frontend**: if a UI gate depends on it, add the permission to `PERMISSION_CAPABILITIES` in
   `libs/auth/src/lib/roles.ts`. The UI is UX only; the server still decides.
6. **Tests**: a unit test for the capability, an integration test for a caller with and without it.

### Add a new seat (role)

Example: `auditor`.

1. **Keycloak**: add the client role `auditor` and assign it.
2. **API**: add it to `ApplicationRoles` (constant, `Canonical` switch, `ByAuthorityDescending`)
   and give it a row in `RoleCapabilities.For(roles)`.
3. **Frontend**: add it to `ROLES` and `CAPABILITIES_BY_ROLE` in `libs/auth/src/lib/roles.ts`,
   and a user to the dev picker if you use the demo.
4. **Local realm**: add the role and a user to `infra/keycloak/realm-aj-boilerplate.json` so
   `docker compose up` can exercise it.

### What happens when roles change at runtime

The Auth service re-mints the JWT before it expires, so a change in Keycloak reaches the API
within the token lifetime. Whenever the roles in the JWT change, the Auth service's
`rolesVersion` changes. The SPA polls it every 15 s, re-reads `/api/v1/me`, and either toasts
(more access) or shows a blocking reload dialog (less access). To push a change sooner, call the
admin refresh endpoint (see the contract).

---

## 5. Configuration reference

Environment-variable form: `:` becomes `__`. "Required" means the process refuses to start (or
a feature stops working) without it.

### Auth service (`AjBoilerplate.Auth`)

Defaults are from `appsettings.json` and the options classes. The Auth service validates all of
this at start-up and refuses to start on a bad value.

| Key | Default | Required | What it does |
|---|---|---|---|
| `ConnectionStrings:AuthDb` | empty | yes | SQL Server database, schema `auth` (system of record for sessions) |
| `ConnectionStrings:Redis` | empty | yes | Session cache, login state, refresh lock, revoked markers |
| `Redis:CaCertPem` | empty | no | PEM of a private CA for Redis TLS |
| `Auth:PublicOrigin` | empty | yes | Browser-facing origin, no path or trailing slash. Builds the redirect URI and checks logout's `Origin`. Must be https while `Cookies:Secure` is true. |
| `Auth:LoggedOutPath` | `/logged-out` | no | Where a plain logout lands. Local path only. |
| `Auth:Cookies:Secure` | `true` | no | `false` drops the `__Host-` prefix and `Secure` flag. Refused outside Development. |
| `Auth:AllowInsecureUpstreams` | `false` | no | Permits plain http to the Entra and Keycloak URLs outside Development. |
| `Auth:Entra:Authority` | empty | yes | `https://login.microsoftonline.com/<tenant-id>`; v2 endpoints are appended |
| `Auth:Entra:ClientId` | empty | yes | App registration client id |
| `Auth:Entra:ClientSecret` | empty | yes | **Secret.** |
| `Auth:Entra:Scopes` | `openid profile offline_access User.Read` | no | Must include `offline_access` |
| `Auth:Entra:RequestOwnClientIdScope` | `false` | no | `true` only for Azure AD B2C |
| `Auth:Entra:AuthorizeEndpointUrl`, `TokenEndpointUrl` | empty | no | Full URLs for an issuer whose endpoints are not `{Authority}/oauth2/v2.0/*` (the local Keycloak stand-in). The first is where the browser goes; the second is called by the service. |
| `Auth:Entra:Timeout` | `00:00:10` | no | HTTP timeout to the issuer |
| `Auth:Keycloak:BaseUrl` | empty | yes | Realm base, no `/realms/...` |
| `Auth:Keycloak:Realm` | `default` | yes | Realm name |
| `Auth:Keycloak:SubjectIssuer` | `oidc` | no | Alias of the Keycloak identity provider that trusts Entra. **Empty** = internal exchange, **refused outside Development**. |
| `Auth:Keycloak:Scope` | `openid profile email` | no | Scope requested on the exchange |
| `Auth:Keycloak:SendAudience` | `false` | no | Send `audience=<portal client id>` |
| `Auth:Keycloak:Timeout`, `MaxAttempts`, `RetryBaseDelay` | `00:00:05`, `3`, `00:00:00.200` | no | Exchange resilience (retries on transient faults only) |
| `Auth:Keycloak:TokenEndpointUrl` | empty | no | Override the exchange URL |
| `Auth:Portal:ClientId` | empty | yes | The portal's Keycloak client |
| `Auth:Portal:ClientSecret` | empty | yes | **Secret.** |
| `Auth:ForwardedHostHeader` | `X-Forwarded-Host` | no | The header carrying the browser-facing host. Read **only** when `Auth:Portals` is not empty, to choose the portal; redirect URIs are built from the registered portal's origin, never from it. |
| `Auth:Portal:DisplayName` | empty | no | The name the "No access" page uses ("You have no access to ..."). Empty reads "this application". |
| `Auth:Portal:SessionEvents:Url` | empty | no | The API's session-ended receiver, e.g. `https://api.example.com/api/integration/auth-backend/session-ended`. Empty: nothing is queued for this portal. |
| `Auth:Portal:SessionEvents:Key` | empty | no | **Secret.** Sent as `X-Auth-Events-Key`. When set, no Google ID token is minted. The API's `Integration:AuthBackend:EventsKey` must equal it. |
| `Auth:Portal:SessionEvents:Audience` | the origin of `Url` | no | The Google ID token audience, used only when no `Key` is set. An origin. |
| `Auth:Portals:N:*` | none | no | A further portal on this Auth service: `Host` (bare DNS name), `ClientId`, `ClientSecret` (**secret**), optional `LoggedOutPath`, `DisplayName`, `SessionEvents:*`. See [handoff.md](handoff.md). |
| `Auth:SessionEndDelivery:*` | see below | no | The session-ended outbox sender. `PollInterval` `00:00:05`, `BatchSize` `50`, `ClaimLease` `00:02:00`, `Timeout` `00:00:10`, `InitialRetryDelay` `00:00:15`, `MaxRetryDelay` `00:15:00`, `MaxAge` `2.00:00:00` (abandoned and logged at Error after this), `Retention` `7.00:00:00`. |
| `Auth:Events:Audience`, `Auth:Events:PushServiceAccount` | empty | no | Both set turns `POST /auth/events` on (Google Pub/Sub push of Keycloak user changes); either empty and it answers 404. |
| `Auth:Admin:ApiKey` | empty | no | **Secret.** Sent as `X-Admin-Api-Key`. Empty (and no Google caller below) makes the whole of `/internal/admin/*` answer 404. |
| `Auth:Admin:Audience`, `Auth:Admin:AllowedCallers` | empty | no | Optional alternative caller authentication: a signature-verified Google ID token for this audience from one of the listed service accounts. Both needed. |
| `Auth:Admin:ManagedGroupPaths` | empty | no | Keycloak group paths the surface may administer, e.g. `/app`. A seat is a group **under** one; any other path is refused and nothing changes. |
| `Auth:Admin:AllowedEmailDomains` | empty | no | Domains a **new** account may be created for. Empty refuses every creation (deny by default). |
| `Auth:Admin:Keycloak:ClientId`, `ClientSecret` | empty | no | The admin client (service account with `view-users`, `query-users`, `query-groups`, `manage-users`). **Secret** for the second. Unset: the user, group and member routes answer 501. |
| `Auth:Admin:Keycloak:BaseUrl`, `Realm`, `Timeout` | the `Auth:Keycloak` ones, `00:00:10` | no | Override where the Admin REST API is. |
| `Auth:Session:EncryptionKey` | empty | yes | **Secret.** Base64 of exactly 32 bytes |
| `Auth:Session:IdleTimeout` | `08:00:00` | no | Sliding |
| `Auth:Session:AbsoluteLifetime` | `12:00:00` | no | From sign-in; activity does not extend it |
| `Auth:Session:RefreshSkew` | `00:02:00` | no | Refresh this long before the JWT expires |
| `Auth:Session:RevokedMarkerLifetime` | `00:15:00` | no | How long a definitive "no role" keeps answering 403 |
| `Auth:Session:LoginStateLifetime` | `00:10:00` | no | A pending sign-in |
| `Auth:Session:RefreshLockLifetime`, `LockPollInterval`, `LockExpiryGrace` | `00:00:05`, `00:00:00.050`, `00:00:00.250` | no | Single-flight refresh lock; the wait must stay under 10 s |
| `Auth:Session:MinimumServableLifetime` | `00:00:05` | no | A JWT closer to expiry than this is not served during a fault |
| `Auth:Session:SweepInterval` | `00:30:00` | no | Expired-row clean-up |

### API (`AjBoilerplate.Api`), identity, admin and session-ended keys

| Key | Default | Required | What it does |
|---|---|---|---|
| `Auth:Mode` | `gateway` (blank also means gateway) | no | The only mode. Any other value stops the API at start-up. |
| `Keycloak:ValidIssuer` | empty | yes\* | Exact `iss` of tokens |
| `Keycloak:JwksUri` | empty | yes\*\* | JWKS URL, pre-warmed at start-up. https outside Development. |
| `Keycloak:Authority` | empty | no | Discovery base, used only when `JwksUri` is empty |
| `Keycloak:ClientId` | empty | yes\* | This portal's client. Selects `resource_access` and the `azp` check, which fails closed when blank. |
| `Keycloak:RequireHttpsMetadata` | `true` | no | Leave on outside local use |
| `Keycloak:AllowDiscoveryFallback` | `false` | no | Outside Development, lets the API resolve keys by discovery when `JwksUri` is empty. Without it that combination stops the API. |
| `Keycloak:AllowInsecureJwksTransport` | `false` | no | **Opt-in.** Lets a non-Development environment fetch the JWKS over plain http. Logs a warning on every start. |
| `Keycloak:HonourRealmRoles` | `false` | no | Read realm-wide roles as seats. Leave off in a shared realm. |
| `AuthService:BaseUrl` | empty | for the admin screens | The Auth service's own origin as the API reaches it (not through the gateway, which answers 404 for `/internal/`). Empty (or no key): every `/api/v1/admin/*` identity route answers 503. |
| `AuthService:AdminApiKey` | empty | for the admin screens | **Secret.** Must equal the Auth service's `Auth:Admin:ApiKey`. |
| `AuthService:Timeout` | `00:00:15` | no | HTTP timeout to the Auth service (1 s to 1 min; anything else uses 15 s). |
| `Integration:AuthBackend:EventsKey` | empty | for sign-in history endings | **Secret.** The shared key of `POST /api/integration/auth-backend/session-ended`; must equal the Auth service's `SessionEvents:Key`. With neither this nor `Audience` set the endpoint answers 404. |
| `Integration:AuthBackend:PortalClientId` | `Keycloak:ClientId` | no | The `portalClientId` an event for this app carries; another value is answered 400. |
| `Integration:AuthBackend:Audience`, `AllowedCallerEmail` | empty | no | The alternative to the key: a Google ID token for this origin from that service account. Both needed. |
| `Portals:N:Key`, `Name`, `Url`, `RequiredCapability` | no entries | no | The list behind `GET /api/v1/me/portals` and the top-bar switcher. `RequiredCapability` is `read`, `write` or `administer`; blank means any signed-in person with a seat; an unknown word hides the portal. |

\* With none of `ValidIssuer` and (`JwksUri` or `Authority`) set, no token validates and every protected endpoint answers 401.
\*\* Outside Development, either `JwksUri` or `AllowDiscoveryFallback=true` is required.

### Web container (nginx)

Read **at container start**, so one image serves every environment. They do not apply to `nx serve`.

| Variable | Default | Used when | What it does |
|---|---|---|---|
| `AUTH_MODE` | `gateway` | always | `gateway` or `dev`. Written to `env.js` for the SPA. Anything else stops the container. `dev` also selects the ungated nginx profile. |
| `AUTH_PROVIDER` | `embedded` | `AUTH_MODE=gateway` | `embedded` (this container is the gateway) or `usp` (static files only). Anything else stops the container. |
| `API_UPSTREAM` | `http://api:8080` | `embedded`, `dev` | Where `/api/` goes. Must not be empty for those profiles. |
| `AUTH_UPSTREAM` | `http://auth:8080` | `embedded` | The Auth service. Must not be empty for that profile. |
| `UPSTREAM_AUTH` | `none` | `embedded` | How the two upstreams above are reached. `none`: plain `proxy_pass`, the browser's `Host`. `cloud-run-id-token`: both are Cloud Run services; nginx loads njs, takes a Google ID token per upstream from the metadata server and sends it as `X-Serverless-Authorization`. Both upstreams must then be `https://`. Anything else stops the container. |
