# ADR-0009: `Idempotency-Key` is opt-in, POST-only, and replays a buffered response from a unique-indexed table

**Status:** Accepted
**Date:** 2026-08-03
**Deciders:** Boilerplate maintainers
**Supersedes:** —

---

## Context

A client that sends `POST /api/v1/items` and never receives a reply — a timeout, a dropped
connection, a load balancer recycling an instance mid-request — cannot tell whether the item was
created. It has two options, and both are wrong. Retrying risks a duplicate; not retrying risks
silently losing the write. In practice clients retry, because the alternative is worse, and the
duplicate lands.

The codebase had nothing that closed this. Two things looked like they might, and neither does:

- **Optimistic concurrency (`RowVersion`)** is on every audited entity and is genuinely load-bearing
  — but it protects an `UPDATE` from a lost update, by refusing a write whose row has moved since it
  was read. A duplicated `CREATE` reads nothing and conflicts with nothing. There is no row to have
  moved. `RowVersion` is silent here, correctly, and the gap is the whole reason for this ADR.
- **The inbox** (`src/backend/src/AjBoilerplate.Domain/Messaging/Inbox/InboxMessage.cs`) *is* an
  at-most-once mechanism, and a good one: it keys on `SourceEventId`, the producer's own event
  identifier, so a redelivered integration event is recognised and not processed twice. But it
  guards the **messaging** edge. Its key comes from the producing system, and it protects a consumer
  loop, not a controller.

Stated plainly, this is the same idea one edge over. The inbox gives at-most-once processing of
inbound *integration events*, keyed by the producer's event id. What was missing is at-most-once
processing of inbound *HTTP writes*, keyed by a label the calling client chooses. Both come down to
the same primitive: write a claim row before doing the work, and let a unique index decide who won.
They share the helper that recognises that race —
`src/backend/src/AjBoilerplate.Application/Persistence/SqlUniqueConstraintViolation.cs` — which
already existed for the inbox and is deliberately narrow (SQL Server errors 2601 and 2627 only, so
it never masks an unrelated database failure as a false success).

The constraints the design had to respect were the ones already in this repository. Every response
is an `ApiResponse<T>` envelope applied by a result filter ([ADR-0005](0005-apiresponse-envelope-and-status-code-contract.md)),
so "return the same response as last time" means the *enveloped, serialised* response, not a
controller return value. `AjBoilerplate.Api` may not reference `AjBoilerplate.Domain`
([ADR-0001](0001-layered-clean-architecture.md), enforced by
`DependencyRuleTests.Api_does_not_reference_domain_directly`), so anything the HTTP edge needs from
the Domain has to arrive through the Application layer. And a boilerplate is consumed by projects
whose deployment shape is unknown, so it may not assume a Redis, a scheduler, or a second process.

## Decision

We will honour an optional `Idempotency-Key` request header on `POST` requests. The first labelled
request executes and its response is stored verbatim; a later request carrying the same key from the
same caller receives that stored response instead of executing again.

The mechanism is:
`src/backend/src/AjBoilerplate.Api/Infrastructure/IdempotencyMiddleware.cs`,
`src/backend/src/AjBoilerplate.Application/Idempotency/IdempotencyService.cs`,
`src/backend/src/AjBoilerplate.Domain/Idempotency/IdempotencyRecord.cs`, and the
`IdempotencyRecords` table added by
`src/backend/src/AjBoilerplate.Infrastructure/Persistence/Migrations/20260803153248_AddIdempotencyRecords.cs`.

The decisions worth recording, each with what it costs and where it is enforced:

1. **Opt-in per request, `POST` only.** No header means unchanged behaviour — the request is not
   inspected, no row is written, nothing is buffered. Requiring the header would have made a
   reliability feature a breaking change for every existing client, and a boilerplate cannot impose
   that. `GET` is already safe to repeat; storing and replaying reads would serve stale data out of
   a table that exists to protect writes. `IdempotencyOptions.Enabled` (default `true`) is an
   operator kill switch for an incident, not an ordinary configuration choice.

2. **A middleware, not an action filter.** What has to be replayed is *the exact bytes the first
   caller received* — envelope, status code, content type, all of it. A filter sees an
   `IActionResult` before `EnvelopeResultFilter` has wrapped it and before the formatter has
   serialised it, so replaying from that position means re-running the wrapping and serialisation
   pipeline and *hoping* it produces the same output. Buffering the response stream captures the
   finished artefact, which is the only version of "the same response" that is actually verifiable.
   The buffer is copied to the real body in a `finally` on every path including an exception,
   because the upstream exception handler writes its 500 into whatever stream is current.

3. **Placed after `UseAuthentication`/`UseAuthorization`, before `MapControllers`**
   (`src/backend/src/AjBoilerplate.Api/Program.cs`). That ordering is a security property, not a
   preference: the key is scoped to the authenticated caller, so the principal must already be
   resolved, and an unauthorized caller must be rejected before it can claim a key or read back a
   stored response. It sits before `MapControllers` because it has to wrap the endpoint that
   produces the response.

4. **The scope is the authenticated subject, never the key alone.** Two clients independently
   choosing the key `1` is an ordinary bug, not a contrived one, and a shared record would leak one
   caller's response body to another. The unique index is on `(Scope, Key)` with `Scope` leading, so
   a record can only ever be found under the identity that created it. A request with no real user
   passes straight through untouched: there is no identity to scope it to, and `[Authorize]` answers
   401 a moment later. A key must never be a route around authentication.

5. **The row is written *before* the work, in `InProgress`.** This ordering is the entire mechanism.
   `IdempotencyService.BeginAsync` looks the key up, and two concurrent requests can both pass that
   lookup — `IX_IdempotencyRecords_Scope_Key` is the only thing that stops both of them executing.
   The loser catches the `DbUpdateException`, recognises it through `SqlUniqueConstraintViolation`
   (the same helper and the same pattern as the inbox), clears change tracking so the rejected
   `INSERT` is not retried, re-reads, and answers from the winner's record: a replay if it has
   completed, a `409` "still processing" if it has not. If the winner has already vanished — a fast
   first attempt that failed and released its claim — the answer is `409`, because this request's own
   `INSERT` was rejected and it holds no claim to execute under.

6. **Only 2xx responses are stored.** A 4xx or 5xx releases the claim, so the key stays usable and a
   genuine retry genuinely retries. Storing a failure would freeze it: a request that failed once on
   a transient fault could never be re-attempted under the same key — and that key is precisely the
   one a well-behaved client retries with.

7. **The key is bound to the request it was first used for.** `RequestHash` is a SHA-256 digest of
   method, path, and body (read through `EnableBuffering` and rewound, so the model binder downstream
   still sees a stream at position zero). Reusing a key for a *different* request returns `409`
   rather than replaying it — which would silently discard the second request while telling the
   caller it succeeded — or executing it, which would break the guarantee the key asked for. The hash
   is checked *before* the status, so a client that has muddled its keys gets an actionable answer
   instead of being told "still in progress" and dropped into a retry loop for work that will never
   be accepted under that key.

8. **A response larger than `Idempotency:MaxResponseBytes` (default 256 KB) releases the key and logs
   an `ERROR`.** It does not store a truncated body, and it does not fail the caller's request — the
   response the caller is receiving is correct. Quietly dropping the stored response would turn a
   later replay back into a second execution, which is the exact failure this feature exists to
   prevent, and it would do so invisibly. The log line names the key, the size, the limit, and the
   consequence. Loud beats silently degraded. `EffectiveMaxResponseBytes` also floors a
   zero-or-negative configured value back to the default, so a typo cannot disable storage outright.

9. **Retention is a documented sweep, not a background service.** `IX_IdempotencyRecords_CreatedAt`
   exists to support it and `Idempotency:RetentionHours` (default 24) states the intended window, but
   nothing in the application enforces either. A boilerplate should not impose a scheduler on a
   consuming project whose deployment shape it does not know. The `DELETE` is written out in
   `src/backend/README.md` under "Idempotency keys".

10. **`MaxKeyLength = 128` is stated twice**, once as `IdempotencyRecord.MaxKeyLength` and once as
    `IdempotencyMiddleware.MaxKeyLength`, because the Api layer genuinely cannot reach the Domain
    constant. This is a real cost of the layering rule, and it is not left to discipline:
    `IdempotencyKeyLimitTests` sees both assemblies and asserts they are equal, so the duplication
    fails the build rather than drifting. The limit matters at the edge — a key longer than the
    column would otherwise be rejected by SQL Server as a 500 instead of by the middleware as a clean
    enveloped 400.

## Consequences

### Positive

- A retried create stops being a coin toss. The client's own retry — the one its HTTP library
  performs automatically — becomes safe, without the server needing to guess intent from the payload.
- The replayed body is the original response byte for byte, returned under the original status code,
  so a client that lost the first reply recovers the *answer* — the `201` and the generated id inside
  the envelope — rather than merely the knowledge that something happened.
- `Idempotency-Replayed: true` on a replayed response means a support engineer reading a HAR file can
  tell a replay from an execution without server access.
- The privacy boundary is structural rather than procedural: a record is unreachable outside the
  identity that created it because the index is keyed that way, not because a query remembered to
  filter.
- Traffic that does not opt in is provably unaffected —
  `IdempotencyApiTests.A_request_without_a_key_is_untouched` asserts two unlabelled identical posts
  still create two items, exactly as before this middleware existed.
- The race is closed by the database, not by application timing. It survives multiple instances,
  which an in-process lock would not.

### Negative

- **Every idempotent `POST` costs extra round trips.** A lookup and an `INSERT` before the work, an
  `UPDATE` after it. That is three additional statements on the request's critical path, in exchange
  for a guarantee the caller asked for.
- **The response is buffered fully in memory** rather than streamed to the client. For the enveloped
  JSON this API returns that is a few kilobytes; for anything larger it is real memory per in-flight
  labelled request.
- **A genuinely streaming endpoint would be broken by this.** Buffering defeats streaming by
  definition. `POST`-only limits the blast radius — the streaming endpoints one usually writes are
  reads — but a project that adds a streaming `POST` must exempt it or accept that it no longer
  streams. This is a real constraint, not a theoretical one.
- **A row per labelled request, growing until someone actually implements the retention sweep.** The
  ADR and the README document a `DELETE`; neither runs it. A boilerplate that documents a cleanup
  nobody runs is a slow leak with good manners, and `ResponseBody` is `varbinary(max)`, so the leak
  is measured in payloads rather than rows.
- **`InProgress` answers `409`, not a wait.** A client that retries eagerly — before the first
  attempt has finished — gets a conflict and has to try again. There is no queueing, no long-poll, no
  server-side wait for the winner. That is a deliberate simplification, and it does push retry timing
  back onto the client.
- **A hard-killed process leaves an orphaned `InProgress` row that blocks its key.** The middleware
  releases the claim in a `catch` and on any non-2xx, but a `SIGKILL`, an OOM, or a host disappearing
  has no `catch` to run. That key then answers `409` for every subsequent attempt until the retention
  sweep — the one nobody is running yet — removes it. Recovering that key today means deleting the
  row by hand.
- **Only one response header is replayed.** `ReplayAsync` sets `StatusCode`, `ContentType`, an
  explicit `Content-Length` (the stored body's length is authoritative; the framework's view of this
  never-executed request is not), `Idempotency-Replayed`, and `Location`. `Location` is stored in its
  own column and restored because it is genuinely *part* of a `201 Created` — a client follows it to
  read back what it created, so dropping it would mean the replay was not the same response, which is
  the one promise this mechanism makes. Every OTHER header is deliberately not stored, and that is a
  real limitation rather than a completed job: an endpoint whose answer lives in a custom header
  (a pagination cursor, a rate-limit hint, an ETag) is replayed without it. The alternative —
  capturing every header — was rejected because most of them are either recomputed correctly for the
  replayed request by middleware that runs upstream (the security headers) or would be actively
  misleading if resurrected from an older request (correlation ids, `Date`), so a blanket capture
  would trade a visible bug for a subtler one. If your API puts contract in a header, add it to the
  record explicitly, as `Location` is.
- **`MaxKeyLength` is duplicated** across two assemblies. A test pins it, so it cannot drift silently,
  but the duplication itself is a cost of ADR-0001's dependency rule and is worth naming as one
  rather than dressing up.

### Neutral

- Two conflict situations share one status code. `409` means both "still processing" and "that key
  was used for a different request"; they are distinguished by the envelope's `message`, not by
  `code` — both carry `EnvelopeCodes.Conflict`. A client that needs to branch on the difference
  currently cannot without reading prose, which ADR-0005 rule 3 says it should not do.
- `IdempotencyRecords` is an `AuditedEntity`, so it carries `CreatedAt`, `UpdatedAt`, and
  `RowVersion` like every other table here, even though the `RowVersion` is not what makes the
  mechanism correct. The unique index is.
- The middleware resolves `IIdempotencyService` and `IActorClaims` from `HttpContext.RequestServices`
  per request rather than by constructor injection, because a middleware is a singleton and these are
  scoped.
- Integration coverage now requires a real SQL Server. A provider that does not enforce a unique
  index would let every one of these tests pass while the mechanism did nothing.

### Follow-on work

- Ship a retention sweep as an opt-in hosted service, off by default, so a project can enable it
  instead of writing the job. The index for it already exists.
- Give `InProgress` and `RequestMismatch` distinct envelope `code` values, declared per ADR-0005
  rule 7, if a client turns out to need to distinguish "retry shortly" from "you have a bug".
- Revisit the 256 KB ceiling once there is evidence of what real responses cost; the current figure
  is a judgement, not a measurement.

## Alternatives considered

### An action filter (`IAsyncResourceFilter`) instead of a middleware

Closer to the endpoint, aware of routing, easily applied per action with an attribute — genuinely the
more idiomatic MVC placement. Rejected on what it can actually capture. A filter sits *inside* the
result pipeline, so it sees an `IActionResult` before `EnvelopeResultFilter` wraps it and before the
formatter serialises it. Replaying from there means re-running the envelope and the serialiser and
asserting the output is identical — a claim that is only true until someone changes a converter, a
naming policy, or the envelope's shape, at which point replays start differing from originals in ways
nothing would catch. The middleware trades routing awareness for the finished bytes, and the finished
bytes are the thing being promised.

### A distributed cache (Redis) instead of an EF table

The conventional choice, with expiry for free — which is precisely the follow-on work this decision
leaves undone. Rejected on two counts. It adds an infrastructure dependency to a boilerplate that
otherwise needs one database, and every consuming project would inherit that requirement whether or
not it uses idempotency at all. More importantly the claim and the work would then live in different
stores: the row this mechanism writes participates in the same database as the write it is
protecting, so "the claim exists" and "the work happened" cannot diverge through a cache eviction or
a failover. Losing atomicity to gain expiry is the wrong side of that trade for a boilerplate. A
project already running Redis at scale should reconsider this — with a new ADR.

### A unique index on business columns instead of a generic key

Put a unique constraint on `Items.Name` and a duplicate create fails on its own. Cheap, no
middleware, no table. Rejected because it solves a different problem: it enforces a *business*
uniqueness rule, which is a decision about the domain, not about retries. It cannot express "these
two requests are the same attempt" for an entity that legitimately allows duplicates, it says nothing
about what response the second caller should receive, and it must be re-derived for every entity that
ever needs the guarantee. The client knows which requests are the same attempt; the server cannot
infer it from the payload.

### Requiring `Idempotency-Key` on every unsafe request

Strongest guarantee, and it makes the behaviour uniform instead of conditional. Rejected because it
is a breaking change to every existing client on the day it ships — a reliability feature that starts
by returning 400 to working callers. It would also force the buffering, the extra round trips, and a
row for *all* write traffic, including the writes nobody retries. Opt-in puts the cost where the
benefit is.

### Replaying stored failures too

Store every response, 4xx and 5xx included, and replay whatever was recorded. Simpler to describe,
and it makes the key's answer perfectly stable. Rejected because it freezes transient faults: a
request that hit a 500 on a database blip could never be re-attempted under the same key, and that is
exactly the key a well-behaved client retries with. The client would have to *change* its
idempotency key to recover from a transient failure, which inverts what the key means. Not storing
failures makes the key's promise narrower and more honest: at most one *success*.

### Doing nothing — rely on client-side dedup or the existing `RowVersion`

The status quo, and it deserves a fair hearing: clients can generate ids client-side, and this API
already has optimistic concurrency. Rejected because neither covers the case. `RowVersion` protects
an `UPDATE` from a lost update by detecting that the row moved since it was read; a duplicated
`CREATE` reads no row and conflicts with nothing, so `RowVersion` is silent and the second item is
created. Client-side dedup, meanwhile, is unenforceable from the server, has to be re-implemented in
every client, and is unavailable to exactly the client that needs it most — the one whose process
died between sending the request and recording that it had sent it.

## Verification

The guarantee is asserted, not asserted-to. All of these run against a containerised SQL Server
(`src/backend/tests/AjBoilerplate.IntegrationTests/Api/IdempotencyApiTests.cs`), which is the only
honest way to test a mechanism whose correctness rests on a unique index:

- **`Concurrent_requests_sharing_a_key_create_exactly_one_record`** — the case the whole thing exists
  for. Eight simultaneous labelled creates; every one of them passes the "not yet claimed" lookup at
  the same instant, and the unique index is the sole reason only one item is written. The
  interleaving is nondeterministic; the assertions are not — exactly one item, and no caller ever
  sees a 500. If this test is deleted or moved to an in-memory provider, the feature is untested.
- **`Two_callers_using_the_same_key_do_not_collide`** — the privacy boundary. Two authenticated users
  posting with the identical key both succeed, and neither reads back the other's response.
- **`A_failed_request_releases_its_key_so_a_retry_really_retries`** — a validation failure under a key,
  then a valid payload under the same key, which must be created rather than refused.
- **`Reusing_a_key_for_a_different_payload_is_refused`** — a `409` with `EnvelopeCodes.Conflict`, and
  no second item.
- Supporting behaviour in the same file: `A_request_without_a_key_is_untouched`,
  `A_replayed_response_says_so`, `A_key_on_a_read_is_ignored`,
  `An_anonymous_request_carrying_a_key_is_still_rejected`,
  `A_malformed_key_is_rejected_with_an_enveloped_400`, and
  `The_stored_record_is_scoped_to_the_caller_and_holds_the_response`.
- **`IdempotencyKeyLimitTests`** pins the duplicated `MaxKeyLength` constant across the Api and Domain
  assemblies; it fails the build if either moves alone.
- Decision logic in isolation:
  `src/backend/tests/AjBoilerplate.UnitTests/Idempotency/IdempotencyServiceTests.cs`, including that
  abandoning never drops a stored response and that a mismatched request is refused even while the
  original is still running.

**The signals that this ADR has stopped serving us**, each of which calls for a superseding ADR
rather than a quiet extension:

- A need for idempotency on `PUT` or `PATCH`. The request-hash binding and the "only 2xx is stored"
  rule were reasoned about for creates; neither is obviously right for a partial update, and
  `IsUnsafeMethod` should not simply grow.
- A need for the guarantee to hold *across services* rather than within one database. The claim's
  atomicity comes from sharing a transaction boundary with the work; the moment the work lives
  elsewhere, that property is gone and the storage choice has to be re-argued.
- Orphaned `InProgress` rows appearing in production. That means the retention follow-on is no longer
  optional, and a boilerplate that ships the mechanism without the sweep is shipping a known defect.
- A second component needing the response buffer — an audit-of-responses feature, say. Two middlewares
  both buffering the same stream is a design that needs deciding, not discovering.

## References

- [ADR-0001: Layered Clean Architecture](0001-layered-clean-architecture.md) — the `Api → Domain`
  prohibition that forces the duplicated constant and the primitives-only service interface
- [ADR-0005: `ApiResponse` envelope and status-code contract](0005-apiresponse-envelope-and-status-code-contract.md)
  — why the replayed artefact must be the serialised envelope, and where `409`/`400` come from
- `src/backend/README.md` § "Idempotency keys" — the client-facing behaviour table and the retention
  `DELETE`
- `docs/architecture.md` § "Outbox and inbox" — the inbound-event dedup this mirrors at the messaging
  edge
- `src/backend/src/AjBoilerplate.Application/Persistence/SqlUniqueConstraintViolation.cs` — the shared
  race-detection helper (SQL Server 2601 / 2627)
