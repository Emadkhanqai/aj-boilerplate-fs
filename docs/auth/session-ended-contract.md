# Session-ended events: the contract between the Auth service and a portal's API

This is what a portal's API builds its receiver against, and what the Auth service's sender does. The producer is
`AjBoilerplate.Auth` (`SessionEvents/`, `Sessions/SessionEndedDispatcher.cs`, `Sql/SqlSessionEndedOutbox.cs`). It is a port of the
USP auth-backend's session-ended events: the wire body and the status semantics are identical, so a receiver written for one works
against the other. Only the way the call authenticates differs, see below.

## Why it exists

A portal that keeps an authentication history sees a user sign in (the gateway puts `X-Usp-Session-Id` and `X-Usp-Session-Started-At`
on every API request, see [gateway-contract.md](gateway-contract.md)) but never sees the session END: a logout, an expiry and a
revoked sign-in all happen inside the Auth service and make no request to the API. This event is how the portal learns about them.

## The call

```
POST {Auth:Portal:SessionEvents:Url}                      e.g. https://api.example.com/api/integration/auth-backend/session-ended
X-Auth-Events-Key:  <shared key>                          when Auth:...:SessionEvents:Key is set   (the default for this boilerplate)
  - or -
Authorization:              Bearer <Google ID token>      when no key is set: a Google ID token minted by the Auth service's own
X-Serverless-Authorization: Bearer <the same token>       service account; aud = the receiver's origin (or SessionEvents:Audience)
X-Request-Id:               <eventId, 32 lowercase hex, no dashes>
Content-Type:               application/json; charset=utf-8
```

- It goes **straight to the portal API**, never through the web gateway: a session that has ended makes no request through the
  gateway, and the gateway's own `/auth/` surface is for browsers.
- With a key, **no Google token is minted at all**, so a deployment off Google Cloud never waits on a metadata server.
- Both calls are authenticated; there is no anonymous mode. A receiver compares the key **in constant time** (hash both sides first,
  then `CryptographicOperations.FixedTimeEquals`, as the admin key check does) and answers `401` to a missing or wrong one.
- Where the Google ID token is used, the receiver verifies it exactly as it verifies any Google-ID-token caller: signature against
  Google's keys, `iss` `https://accounts.google.com`, `aud` the receiver's origin (no path), `email_verified=true` and `email` the Auth
  service's service account, not expired. Read it from `Authorization`; the platform consumes `X-Serverless-Authorization`.
- The endpoint is a **machine surface**: keep it out of the Keycloak/JWT scheme, out of sign-in recording and out of anything that
  reads `X-Usp-Session-Id` (it carries none).

## Payload

```json
{
  "schemaVersion": 1,
  "eventType": "session.ended",
  "eventId": "0192f0a0-0000-7000-8000-000000000001",
  "sessionId": "0123456789abcdef0123456789abcdef",
  "reason": "session-expired",
  "reasonDetail": "idle_timeout",
  "occurredAtUtc": "2026-10-22T09:15:42Z",
  "sessionStartedAtUtc": "2026-10-22T01:00:07Z",
  "identityProvider": "Auth Backend (Keycloak)",
  "portalClientId": "my-app",
  "correlationId": null
}
```

Every field is always present, in this order; `null` only where marked.

| Field | Type | Meaning |
|---|---|---|
| `schemaVersion` | int | `1`. A receiver that sees another value answers `400`. |
| `eventType` | string | `session.ended`. The only type. |
| `eventId` | string, UUID (v7) | **Idempotency key.** Identical on every retry of this event. |
| `sessionId` | string, 32 lowercase hex | The same value as `X-Usp-Session-Id` on that session's requests: the key the portal's "session created" record was written against. **Pair on this.** |
| `reason` | string | `logged-out`, `session-expired` or `signed-out-by-system`. Store the wire value; show "Logged out", "Session expired", "Signed out by system". |
| `reasonDetail` | string | Which check decided it (below). For investigation, not the user. New values may be added: **never reject an unknown one.** |
| `occurredAtUtc` | string, `yyyy-MM-ddTHH:mm:ssZ` | When the session ended (for an expiry, the deadline that passed). Use as the record's event time. |
| `sessionStartedAtUtc` | string or `null` | The sign-in time, same format and value as `X-Usp-Session-Started-At`. `null` if none was recorded. |
| `identityProvider` | string | The constant `Auth Backend (Keycloak)`. |
| `portalClientId` | string | The Keycloak client of the portal the session belonged to (`Auth:Portal:ClientId`, or `Auth:Portals:N:ClientId`). A receiver may answer `400` for another client's event. |
| `correlationId` | string or `null` | The request id (`X-Request-Id`) of the request that ended the session; `null` for an expiry the sweeper found. |

`reasonDetail` values and where each comes from:

| `reason` | `reasonDetail` | Ended by |
|---|---|---|
| `logged-out` | `user_logout` | `POST /auth/logout` |
| `session-expired` | `idle_timeout` | the sweeper, after `Auth:Session:IdleTimeout` |
| `session-expired` | `absolute_lifetime` | the next `/auth/validate`, or the sweeper, after `Auth:Session:AbsoluteLifetime` from sign-in |
| `signed-out-by-system` | `entra_invalid_grant` | the identity provider refused the refresh grant (the user's verdict) |
| `signed-out-by-system` | `keycloak_denied` | Keycloak's definitive "no" on the token exchange |
| `signed-out-by-system` | `no_client_role` | Keycloak minted a token with no role for this client |
| `signed-out-by-system` | `no_refresh_token` | a session with nothing to renew from |
| `signed-out-by-system` | `refresh_lineage_ended` | a handed-off session whose source session is gone |
| `signed-out-by-system` | `user_disabled` | `POST /internal/admin/users/{subject}/disable` |

**There is no user identifier, token, cookie or e-mail address in the event, by design.** Take the user from the portal's own
"session created" record with the same `sessionId`.

An infrastructure fault (identity provider or Keycloak unreachable, a lapsed refresh lock, Redis or SQL errors) keeps the session and
so ends nothing and reports nothing. Replacing a session (a second sign-in) is not an end either.

## What the receiver does

1. Authenticate (above). Failure: `401` (or `403`).
2. Validate the shape: `schemaVersion == 1`, `eventType == "session.ended"`, `eventId` a UUID, `sessionId` 32 lowercase hex, `reason`
   one of the three, `occurredAtUtc` parses. Failure: `400`.
3. **Already recorded this `eventId`** (or already has an end record for this `sessionId`): answer `200 {"outcome":"duplicate"}` and
   write nothing.
4. **No "session created" record for this `sessionId`**: answer `200 {"outcome":"ignored-unknown-session"}` and **write nothing**.
   This is normal, not an error: the session never reached this API, or was created before sign-in recording existed. Never store a
   record you cannot attribute.
5. Otherwise insert one record (event type e.g. `SessionEnded`, time `occurredAtUtc`, user copied from the "session created"
   record), under a unique key on (`sessionId`, event type) so a concurrent duplicate hits the index and is answered as `duplicate`.
   Answer `200 {"outcome":"recorded"}`.

The response body is optional. The Auth service logs `outcome` if it is one of the three values above and treats any `2xx` as
delivered; any other text in a body is never logged.

## Delivery semantics (producer side)

- **At least once.** Deduplicate on `eventId` (and on `sessionId` + event type). No ordering guarantee across events. At most one
  event per `sessionId` is ever produced.
- **Written with the end, never without it.** The event row is inserted in the SAME SQL transaction as the delete that ends the
  session (`auth.SessionEndedOutbox`), so a session cannot end without its event, nor an event exist for a session that did not end.
  Only the call whose `DELETE` removed the row queues an event, so a logout racing the sweeper produces one.
- **Retry:** on timeout (`Auth:SessionEndDelivery:Timeout`, 10 s), transport error, `5xx`, `408`, `429`, **`404`** (receiver not
  deployed yet) and **`401`/`403`** (key or caller not configured on the receiver yet). Backoff 15 s, doubling, capped at 15 min, for
  up to **48 h** after the event was queued; then abandoned and logged at `Error`.
- **Give up at once** on any other `4xx` (`400`, `409`, `413`, `415`, `422`, ...): resending the same bytes cannot succeed. So a
  receiver answers `400` only for an event that is malformed for good.
- **Several instances are safe.** An event is claimed by a conditional SQL update (not a Redis lock), so two instances never send one
  event inside a claim lease, and a Redis outage cannot stop delivery. A claimant that dies simply lets its lease run out.
- **Which portal:** the portal whose Keycloak client the session was created for (`AuthSession.ClientId`). A hand-off makes a separate
  session on the target portal, and each portal gets its own event.
- **Inert until configured, per portal.** The Auth service queues nothing for a portal until its `SessionEvents:Url` is set, and the
  sessions that end before that are never reported. **Deploy order: the receiver first, then the key (or the invoker grant), then
  the URL on the Auth service.**
- **Latency:** a logout or a refresh verdict is sent within seconds (`Auth:SessionEndDelivery:PollInterval`); an idle expiry up to one
  sweep interval (`Auth:Session:SweepInterval`, 30 min) after its deadline.
- Without durable sessions (a Redis-only run) there is no outbox and nothing is reported. The shipped Auth service is always durable.

## Auth service configuration (names only)

```
Auth__Portal__SessionEvents__Url        https://api.example.com/api/integration/auth-backend/session-ended
Auth__Portal__SessionEvents__Key        shared key, from the secrets provider (omit to use a Google ID token)
Auth__Portal__SessionEvents__Audience   optional; the Google token audience, default the origin of Url, and must be an origin
Auth__Portals__N__SessionEvents__*      the same three, for a further portal
Auth__SessionEndDelivery__*             PollInterval, BatchSize, ClaimLease, Timeout, InitialRetryDelay, MaxRetryDelay, MaxAge, Retention
```

The receiver's side of the key is the API's own configuration (the name is the API's choice; see the API's README). The Auth service
logs every send with `EventId`, `Portal`, `Surface=session-ended`, `Status`, `Outcome` and `Attempt`; never the session id, the key
or a token.
