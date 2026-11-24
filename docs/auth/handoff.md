# Cross-portal sign-in hand-off

One Auth service can sign users in to **more than one portal**, each on its own host. Every portal's session cookie is host-only
(`__Host-app_sid`), so a user signed in to portal A who follows a link to portal B arrives with no session there and would be sent
through the identity provider again. The hand-off lets portal A give portal B a session for the same person with **no identity
provider round trip**.

It is a port of the USP auth-backend's hand-off (`/auth/handoff` and `/auth/handoff/redeem`), with the same paths, the same
semantics and the same security properties. **It does nothing in a single-portal installation**: with `Auth:Portals` empty there is
nobody to hand off to, and `GET /auth/handoff?to=<anything but this portal's client id>` is a plain 404 page.

## Configuring more than one portal

Portal #0 is `Auth:Portal` (its host is the host of `Auth:PublicOrigin`). Further portals are `Auth:Portals:N`:

```
Auth__Portals__0__Host           app2.example.com                bare DNS host, no scheme, no port; unique across portals
Auth__Portals__0__ClientId       app2                            the Keycloak client; unique; also the hand-off KEY
Auth__Portals__0__ClientSecret   (secrets provider)
Auth__Portals__0__LoggedOutPath  /logged-out                     optional; default Auth:LoggedOutPath
Auth__Portals__0__DisplayName    App Two                         optional; named on the "No access" page
Auth__Portals__0__SessionEvents__*                               optional; see session-ended-contract.md
```

Once a second portal exists the portal of a request is chosen by the gateway's `X-Forwarded-Host` (`Auth:ForwardedHostHeader`); an
unknown host answers 404 on `/auth/login`, `/auth/logout`, `/auth/no-access` and the hand-off, and 401 on `/auth/validate`. Each
portal's redirect URI is **its configured origin plus `/auth/callback`**, never built from the header that chose it. A session is
valid only on the portal whose client it was created for, and a sign-in started on one portal cannot be completed on another. Every
portal's registered redirect URI must be added to the identity provider's app registration.

## The two endpoints

Both live under `/auth/`, which the gateway already proxies on every portal host without `auth_request`.

### `GET /auth/handoff?to=<client id>&rd=<path>` on the SOURCE portal

`to` is the target portal's **Keycloak client id**, looked up in the configured portals: a key into an allow-list, never a URL, so it
cannot become an open redirect.

1. Unknown source host: `404`. Unknown `to`: a `404` page. `to` is this portal: `303` to `rd` here.
2. `Sec-Fetch-Site: cross-site`: mint nothing, `303` to `<target origin>/auth/login?rd=<rd>`. (A third-party page can navigate a
   victim's browser here; minting would cost an identity-provider refresh per hit and gain the attacker nothing.)
3. No session, a session of another portal, or one past its absolute lifetime: `303` to the target's ordinary sign-in.
4. Otherwise mint a ticket: 256 random bits, stored in Redis under `auth:handoff:<SHA-256 of the ticket>` for
   `Auth:Session:HandoffTicketLifetime` (60 s), holding the source session's storage key, its subject, the source and target
   portals and `rd`. **The ticket names a session, not a person.**
5. Answer `200` with a page that **POSTs** the ticket to `<target origin>/auth/handoff/redeem` from a hidden field,
   auto-submitted by one script allowed by CSP hash, with a `<noscript>` Continue button.

### `POST /auth/handoff/redeem` on the TARGET portal

1. **Take the ticket first** (read + delete in one Lua script), before any other check, so every presentation consumes it, including
   one that is then refused.
2. Refuse, falling back to `303 /auth/login?rd=<rd>` on this portal, when: the ticket is unknown, used or expired; it was minted for
   another portal; `Origin` is not the ticket's source origin; the source session is gone, belongs to another portal or subject, or is
   past its absolute lifetime; the identity provider or Keycloak fails transiently; the minted token's `sub` differs from the source
   session's; or anything throws.
3. A definitive Keycloak no-role, or a token minted for another client: `303` to `/auth/no-access` with a one-shot marker cookie. Not
   a redirect to sign-in: that would loop through the identity provider's silent SSO.
4. If the browser already holds a live session on this portal for the same subject, reuse it: no provider call.
5. Otherwise mint the target session, set `__Host-app_sid` on this host and `303` to `rd`.

`GET /auth/handoff/redeem` redeems nothing: it answers `303 /`. `GET /auth/no-access` renders the "No access" page once (the marker
cookie is spent by the view) and sends any later view to `/`.

## How the target session is minted: it SHARES the source's refresh token

An identity-provider refresh token **rotates on use**, so one grant cannot back two independent sessions: giving the new token to the
target would kill the source's copy (its next refresh would get `invalid_grant` and end the source session), keeping it in the source
would leave the target none, and sharing a copy lets whichever refreshes second fail. Each would manufacture the very sign-out this
feature exists to remove.

So **the target session holds no refresh token of its own.** It records `RefreshVia`: the storage key of the session that does (the
*lineage root*). One grant, several portal sessions:

- Redeem and every later refresh of a derived session take the refresh lock **of the lineage root** (`refresh:<root key>`), read the
  root's refresh token and write the rotated one back to the root at once, before Keycloak is asked. Single-flight is per GRANT.
- A hand-off from a derived session points at the same root, so lineage is always one level deep.
- A derived refresh also slides the root's idle TTL, so working all day in portal B does not let the idle portal A session expire
  under it.
- A refresh that acquired the lock only after waiting never ends a session on `invalid_grant`: the previous holder may still be in
  flight with the same token, so the verdict cannot be attributed to the user.
- A derived session whose root is gone (signed out, swept) ends at its next refresh as `signed-out-by-system` /
  `refresh_lineage_ended`. **Signing out of the root therefore stops its derived sessions renewing.**

The target's `CreatedAt` is **the source's**, so every session in a lineage ends at the same moment, `Auth:Session:AbsoluteLifetime`
after the original sign-in; a hand-off can never extend a session's life.

## Security properties

| Property | Mechanism |
|---|---|
| One use only | read + delete in one Lua script before any check |
| Not replayable on another portal | the ticket records its target client; redeem compares it with the resolved portal |
| Cannot carry another subject | the ticket names a session; its subject is compared with the session's and with the minted token's `sub` |
| Not in any log | a POST body; no log line takes the ticket or its hash (a test captures every line at every level and asserts it absent) |
| Not leaked via Referer | not in any URL; redeem answers `303`, never a page |
| No login CSRF | `Origin` must equal the ticket's source origin (the page declares `strict-origin`, because under the gateway's `no-referrer` a browser sends `Origin: null`) |
| No open redirect | `to` is an allow-list key; `rd` goes through `RedirectTarget` |
| Bounded by the absolute lifetime | the target inherits the source's sign-in time; an expired source is refused |
| Rate limited | the gateway's `limit_req` on `location /auth/` |
| Never ends a session | refusal is the ordinary sign-in; nothing on the hand-off path deletes a session |

## What the gateway needs

- `X-Forwarded-Host $host` on `location /auth/` (already set): it is what chooses the portal.
- **The hand-off page carries its own `Content-Security-Policy`** (`script-src 'sha256-…'`, no `form-action`), because it runs one inline
  script and posts a form to ANOTHER origin. The gateway's `security-headers.conf` CSP (`script-src 'self'`, `form-action 'self'`)
  would be intersected with it and block both. For `location = /auth/handoff` the gateway must therefore pass the Auth service's CSP
  through as the only one (hide the upstream's, or do not include the snippet's, never both).
- `/auth/events` and `/auth/validate` stay 404 at the gateway; `/internal/` stays 404.

## Schema

`auth.Sessions.RefreshVia` (`char(43)`, nullable, additive) arrives with the migration `AddHandoffAndSessionEndedOutbox`, which also
creates `auth.SessionEndedOutbox`. Run it before the new revision rolls out; the previous revision ignores the column.
