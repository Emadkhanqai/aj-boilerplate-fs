# Auth troubleshooting

Find your symptom, read the likely cause, run the check. For how the parts fit together see the
[overview](README.md); for configuration see [setup](setup.md).

**First, two habits that save an hour:**

1. **Find out which part answered.** A browser sees one site, but four parts sit behind it. Open
   the browser's network tab and look at the status and response headers of the failing request.
   `X-Usp-Access: revoked`, `X-Usp-Failure: auth` and an `X-Request-Id` tell you where to look.
2. **Join the browser to a log line.** nginx stamps every request with `$request_id`. The gateway's
   error pages print it as "Reference", the Auth service logs it as `CorrelationId`, and nginx
   passes it to the API as `X-Request-Id`. Search the logs for that value.

```bash
docker compose logs -f auth      # sign-in, refresh, exchange failures
docker compose logs -f api       # token rejection (azp, issuer, signature)
docker compose logs -f web       # nginx access log (JSON, no query strings, no credentials)
docker compose logs -f keycloak
```

---

## Setting up locally

### `docker compose up` hangs, or `db` never becomes healthy

**Cause: a stale `aj-boilerplate_db-data` volume** from an earlier checkout whose `.env` had a
different `MSSQL_SA_PASSWORD`. SQL Server reads the `sa` password only when it first creates
its data files. A later password is ignored, so the health check logs in with the new password,
fails, and everything that waits for `db` waits forever.

```bash
docker compose logs db | tail         # look for: Login failed for user 'sa'
docker compose down -v                # destroys the local database volume, then:
docker compose up --build
```

If you want to keep the old data, restore the original `MSSQL_SA_PASSWORD` in `.env` instead.
(A different compose project name does not help while the old containers still exist: the
containers have fixed names such as `aj-db`.) To remove just that one volume:
`docker volume rm aj-boilerplate_db-data`.

### A port is already in use

Every published port has an override in `.env`: `WEB_PORT`, `API_PORT`, `KEYCLOAK_PORT`,
`DB_PORT`, `REDIS_PORT`. If you change `WEB_PORT` or `KEYCLOAK_PORT`, **recreate Keycloak**:
the realm substitutes `WEB_PORT` into the sign-in client's redirect URI only when it is first
imported, and an existing Keycloak container keeps the old value.

```bash
docker compose up -d --force-recreate keycloak
```

Do not run the root compose file together with `src/backend/docker-compose.yml` or
`src/frontend/docker-compose.yml`. They publish the same ports.

### Keycloak is "not ready" for a long time

The first start imports the realm and can take a minute or more. `auth` and `api` wait for
Keycloak's health check (it retries for about five minutes). If it never turns healthy:

```bash
docker compose logs keycloak | tail -50
```

Keycloak in this stack is dev-mode with its data inside the container. Recreating the container
resets users and settings to the committed realm; a plain restart keeps them.

### "Invalid parameter: redirect_uri" at Keycloak, or `invalid_redirect_uri`

The redirect URI the Auth service sends is `<Auth__PublicOrigin>/auth/callback`. It must match
the sign-in client's registered URI exactly. Locally that is `http://localhost:<WEB_PORT>/auth/callback`,
so **open the app at `http://localhost:4200`, not `http://127.0.0.1:4200`**. See also the port
note above. With a real Entra tenant the same rule applies to the Web redirect URI of your app
registration; Microsoft reports a mismatch as `AADSTS50011`.

---

## Signing in

### I am sent back to `/login` over and over (a 401 loop)

`nginx` turns a `401` from `/auth/validate` into `302 /login?from=…`, and the SPA starts sign-in
automatically. If you land back on `/login` after signing in, the session cookie is not being
accepted. Check, in this order:

| Check | How | Fix |
|---|---|---|
| Cookie is set at all | Browser dev tools → Application → Cookies. Expect `app_sid` (local http) or `__Host-app_sid` (https). | See "cookie is not set" below. |
| Host matches `Auth__PublicOrigin` | The address bar must be the origin you configured. | Use that origin. |
| The Auth service can read its stores | `curl -s http://localhost:4200/healthz` is nginx only. Check `docker compose ps` for `auth` health and `docker compose logs auth`. | Fix Redis or SQL. |
| The session really expired | 8 h idle, or 12 h from sign-in regardless of activity. | Sign in again. This is by design. |

The SPA has a brake so it cannot loop forever: it auto-starts sign-in once per five minutes per
tab. After that the login page shows a message and waits for a click.

### Signed in, but every API call is a `401`

The browser has a session, nginx accepted it and sent the API a JWT, and the **API** rejected it.
The API log says why:

| API log line | Cause | Fix |
|---|---|---|
| `Rejected a token issued for a different client (azp=…)` | `Keycloak__ClientId` on the API differs from `Auth__Portal__ClientId` on the Auth service, or the exchange minted the token for another client | Make them the same string |
| `IDX10205` / issuer mismatch | `Keycloak__ValidIssuer` is not the exact `iss` of the token. Locally `iss` is the address the **browser** uses for Keycloak (`http://localhost:8081/realms/aj-boilerplate`), not `keycloak:8080`. | Copy the `iss` from a decoded token |
| `IDX10500: Signature validation failed. No security keys were provided` | JWKS not reachable | Check `Keycloak__JwksUri` from inside the API container |
| `IDX10223` / "token is expired" | Clock skew. The API accepts **no** skew (`ClockSkew = 0`). | Sync the host clocks (NTP) |
| nothing, and `Keycloak__*` is unset | With no Keycloak configuration no token can validate | Set `Keycloak__ValidIssuer` and `Keycloak__JwksUri` |

### Everyone who signs in gets the "No access" page

The Auth service shows **403 "No access"** when Keycloak says the user has no role, **and also**
when Keycloak minted the token for a different client than `Auth__Portal__ClientId`, or when the
token carries no role for that client. In the second case the Auth log says
`Sign-in refused: Keycloak minted that token for client …, not for …`. Check:

- `Auth__Portal__ClientId` is the client that has the roles;
- that client has the client roles and the **flat `roles`** mapper (setup, 2b);
- the user actually holds a role on it.

### "Sign-in temporarily unavailable" (503)

That is an infrastructure fault, not a verdict about you, and it never signs anyone out. Read
`docker compose logs auth`. Typical lines:

- `Keycloak rejected OUR client … check its secret and token-exchange permission`: wrong
  `Auth__Portal__ClientSecret`, or token exchange is not enabled for that client. This is
  deliberately classed as a fault so a misconfiguration cannot masquerade as "user has no access".
- Entra or Keycloak unreachable, a timeout, SQL or Redis down.
- `AADSTS650053`: `Auth__Entra__RequestOwnClientIdScope` is `true` against Entra. Leave it `false`
  (it is for Azure AD B2C only).

### "Sign-in expired" (400)

The login `state` is single use and lives 10 minutes. It also needs the `app_login` cookie it was
bound to. Causes: you reused an old link, opened sign-in in two tabs, took more than 10 minutes, or
your browser dropped the cookie (see below). Start again from the app.

### The session cookie is not set (plain http)

Browsers refuse the `__Host-` prefix and `Secure` cookies on plain http. The local stack sets
`Auth__Cookies__Secure=false`, which switches the Auth service to `app_sid` and `app_login`
without `Secure`. If you run the Auth service some other way over http, set that variable too.
It is **refused outside the Development environment**, so a real deployment must use https and
the `__Host-` names. Also confirm the host in the address bar equals `Auth__PublicOrigin`.

### Sign-out goes straight back in, or signs me in as someone else

Sign-out removes the application session and leaves the Entra single-sign-on session alone, by
design. The SPA marks the browser so the **next** sign-in sends `prompt=login`, which forces the
account prompt. The "signed out" page never redirects by itself; it waits for a click.

`POST /auth/logout` answers **403** if the request's `Origin` is not `Auth__PublicOrigin`
(or, when `Origin` is absent, `Sec-Fetch-Site` is not `same-origin`). Check the public origin
setting if sign-out fails behind a proxy.

---

## After signing in

### `403` that means "no access" versus `403` that means "your access was removed"

| What you see | Meaning | Where it comes from |
|---|---|---|
| Browser navigation gets a "You have no access" page | The user has no role for this portal | nginx, on a 403 from `/auth/validate` |
| An API call answers `403` with `X-Usp-Access: revoked`; the SPA shows an **access-revoked dialog** whose only button signs out | Keycloak said, definitively, that the user no longer has a role. The Auth service remembers it for 15 minutes. | Auth service, relayed by nginx |
| An API call answers `403` **without** that header | The policy denied this action. The user is signed in but lacks the seat or permission for this endpoint. | The API |

After the 15-minute marker lapses, the same session answers `401` and the user signs in again.

### `503` with `X-Usp-Failure: auth`; a "Temporarily unavailable" page

The Auth service could not reach Keycloak, Entra, Redis or SQL **and** the session's current JWT
has less than 5 s left. Nothing about the user's session was ended. The SPA shows an
**"access check unavailable"** page when it cannot read `/api/v1/me`, which means "we could not
ask", not "you may not". Fix the dependency and reload.

### nginx answers `500` for every gated request

`docker compose logs web` shows `upstream sent too big header while reading response header from
upstream`. The JWT that `/auth/validate` returns in `X-Auth-Jwt` is bigger than nginx's default
4k header buffer. The shipped gateway sets `proxy_buffer_size 8k` on `/_auth_validate` for this.
If you customise the template, keep it, and raise it if a user holds so many roles that the JWT
outgrows 8k.

### `GET /auth/validate` or `/internal/...` returns 404 in the browser

Expected. nginx hides them on purpose; only nginx calls `/auth/validate`, and `/internal/` is for
trusted callers inside your network.

### `429 Too Many Requests` on `/auth/*`

nginx rate-limits by the peer address. Behind a load balancer the peer is the balancer, so all
users share one bucket. Configure `set_real_ip_from` and `real_ip_header` for your balancer in
your own nginx configuration. (Sign-in traffic and the SPA's 15-second `roles-version` polling
use separate budgets.)

### The user's roles changed and the app did not notice

The SPA polls `/auth/roles-version` every 15 s while the tab is visible and again after any 403.
A role change shows up after the JWT is next re-minted (the Auth service re-mints within two
minutes of expiry) or immediately after an admin `refresh` call. A background tab does not poll.

---

## The API refuses to start

| Message (abridged) | Meaning | Fix |
|---|---|---|
| `'Auth:Mode' is '…', which is not a recognised mode` | A typo cannot select a weaker mode, because there is only one | Remove it or set `gateway` |
| `Keycloak is configured but 'Keycloak:JwksUri' is empty, and this is not the Development environment` | Without an explicit key endpoint, requests would stall on discovery | Set `Keycloak__JwksUri`, or set `Keycloak__AllowDiscoveryFallback=true` after confirming discovery works from that network |
| `'Keycloak:JwksUri' is not an https URL, and this is not the Development environment` | A plaintext JWKS can be substituted on the network path, which would let forged tokens through | Use an https JWKS URL. If your platform genuinely has none, set `Keycloak__AllowInsecureJwksTransport=true` **with a dated, written reason**. The API then starts and logs a warning on every start. |

## The Auth service refuses to start

It validates its configuration at start-up and prints every failure at once, naming each key,
for example `Auth:PublicOrigin must be https while Auth:Cookies:Secure is true`,
`Auth:Session:EncryptionKey must be base64 of exactly 32 bytes`,
`Auth:Keycloak:SubjectIssuer is empty … refused outside Development`, or
`… is http. Plain http to an upstream that receives a client secret is refused`.
Fix the named key. A missing `ConnectionStrings:AuthDb` or `ConnectionStrings:Redis` also stops it.

---

## Still stuck

1. Reproduce with `docker compose up --build` from a clean state (`docker compose down -v`).
   If that works, the difference is configuration, not code.
2. Compare your setting with the [reference table](setup.md#5-configuration-reference).
3. Read the invariants in the [contract](gateway-contract.md#invariants-never-break-these). A fault
   must never sign anyone out, and only a `401` may become a login redirect. If you are seeing
   otherwise, you have found a bug; open an issue with the `Reference` value.
