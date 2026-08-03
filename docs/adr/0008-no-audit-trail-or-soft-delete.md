# ADR-0008: No audit trail and no soft delete — the ingredients ship, the policy does not

**Status:** Accepted
**Date:** 2026-08-03
**Deciders:** Boilerplate maintainers
**Supersedes:** —

---

## Context

Two features are conspicuously absent from this boilerplate, and both are absent on purpose.

**There is no audit trail.** No `AuditEntry` aggregate, no audit table, no `SaveChanges`
interceptor. `AppDbContext` maps six tables — `Items`, `feat_Features`, `feat_Acknowledgements`,
`OutboxMessages`, `InboxMessages`, `IdempotencyRecords` — and none of them is one.

**There is no soft delete.** No `IsDeleted` column, no `ISoftDeletable`, no
`HasQueryFilter` anywhere in the model. `ItemService.DeleteAsync` calls `_items.Remove(item)` and the
row is gone.

Both omissions are easy to misread, for a specific reason: the repository is full of things that
*look* like they are about to deliver them.

- `AuditedEntity` is called `AuditedEntity`.
- `ICorrelationContext`'s own doc comment says it exists "so a domain mutation's audit entries can be
  tied back to the API call that produced them".
- `PrivacyHash` says it is for "any other directly-identifying value an audit row still needs to
  correlate on".
- `docs/architecture.md` §Correlation ids lists "an audit entry" among the four things a trace id
  ties together.
- `.claude/standards/security.md` §Audit *requires* an "append-only audit log of every
  business-significant action: actor, timestamp, action type, and prior → new values", and
  `.claude/standards/middleware.md` §13 says where it belongs.

None of those is wrong. They describe **ingredients and requirements**, not a shipped feature. This
ADR exists because the gap between them and the code is otherwise indistinguishable from an
oversight.

### What `AuditedEntity` actually gives you, precisely

`AuditedEntity` carries three things: `CreatedAt`, `UpdatedAt`, and a database-issued `RowVersion`.
That is **record-level provenance**, and it is not an audit trail. The distinction is not pedantry —
it is the difference between a fact about a row and a history of what happened to it.

What it gives you:

- When the row was first persisted, and when it was last modified.
- An optimistic-concurrency token that the engine enforces inside the `UPDATE` statement.

What it does not give you, and cannot:

- **Who.** There is no actor column anywhere on it. `AuditedEntity` records *when*, never *by whom*.
- **What changed.** No before, no after, no field-level delta.
- **History.** `UpdatedAt` is overwritten on every change. After forty edits the row remembers the
  fortieth and nothing else. An audit trail is append-only; this is a two-slot, last-write-wins
  summary living on the mutable row itself.
- **Anything that did not change a row.** A read of a confidential record, a denied authorisation, a
  bulk export, a failed login. These are usually the first events an auditor asks about, and no
  entity base class will ever see them.
- **Anything about a row that no longer exists.** A hard delete takes `CreatedAt` and `UpdatedAt`
  with it.

`RowVersion` is likewise a concurrency token, not a version history: it tells you the row moved, not
what it moved from.

### Why a generic audit trail would be the wrong shipped default

An audit trail's shape is determined entirely by what the business must prove and to whom. A
regulator wants a defined retention period and demonstrable immutability. A customer-dispute process
wants before/after values on specific fields in language a non-engineer can read. Internal forensics
wants actor, source address, and correlation across services, and does not care about field deltas at
all. Those three requirements produce three different schemas, three different retention policies,
three different access-control models, and three different answers to "must the record survive the
deletion of the thing it describes?"

A boilerplate that picks one is either too weak to satisfy a real requirement or too heavy for a
project that has none. The worse of those two failures is the first, and it fails quietly: **a
half-configured audit trail looks like compliance.** A table called `AuditLog` full of
`UpdatedAt: 2026-08-01 → 2026-08-02` rows will be pointed at in a review, and will not survive
contact with an actual auditor.

### Why soft delete is not free

Soft delete is the more famous of the two, and famously leaky. The standards this repository ships
already say so — `.claude/standards/efcore-migrations.md` §Data lifecycle: *"Soft delete only where
the business requires it — not as a blanket pattern. A soft-delete flag that every query must
remember to filter is a bug generator."* The costs are concrete, and this schema has an example of
each:

- **Every query must filter it, and one forgotten filter is a data leak** — not a wrong count, a leak
  of records someone was told had been deleted.
- **Unique indexes stop working.** `IX_feat_Acknowledgements_User_Feature` is unique on
  `(UserId, FeatureId)` and is described in its own configuration as a correctness constraint. Under
  soft delete it must become a filtered index or a deleted acknowledgement permanently blocks a new
  one for the same pair. Filtered indexes then only serve queries that carry the same predicate.
- **Foreign keys and cascades stop meaning what they say.**
  `FeatureAcknowledgementConfiguration` declares `OnDelete(DeleteBehavior.Cascade)`, and the comment
  explains why the acknowledgements must go with their announcement. A soft delete is an `UPDATE`;
  no cascade fires. The declared behaviour becomes decorative and the real cascade has to be
  hand-written and hand-remembered.
- **EF Core global query filters apply to some paths and not others.** They apply to LINQ queries
  rooted on the entity and to navigations loaded through it. They do not apply to raw SQL executed
  outside the LINQ pipeline, to stored procedures, to reporting views, to migrations, or to any BI
  tool or support script reading the table directly. `IgnoreQueryFilters()` switches them off for a
  query in one method call that no compiler will question. And a *required* navigation pointing at a
  filtered principal can materialise as null — EF Core warns about that interaction precisely because
  it surprises people.
- **The API contract shifts underneath you.** `ItemService.DeleteAsync` returns `false` for "no such
  item", which `ItemsController` turns into a `404` per [ADR-0005](0005-apiresponse-envelope-and-status-code-contract.md).
  Under soft delete, "already deleted" and "never existed" become different states that both have to
  map onto that contract, and a client holding a `RowVersion` from before the deletion now gets a
  `409` where it used to get a `404`.

### The distinction that is actually the point

This repository already has two entities that retire without being deleted, and neither of them is
soft delete:

- `Item.Archive()` / `Item.Restore()`, where `ItemStatus.Archived` is a real state with a real
  invariant — `Item.Update` refuses to edit an archived item and tells you to restore it first.
- `FeatureAnnouncement.Retire()` / `Reinstate()`, where `IsActive` is false means the announcement
  stops being shown while its acknowledgement rows stay intact, so "who has seen this?" is still
  answerable.

Both are **domain-meaningful lifecycle states**: named in the ubiquitous language, enforced by
behaviour methods, with rules attached. Infrastructural soft delete is the opposite — a
meaning-free flag applied uniformly to everything, whose only rule is "pretend this is not here". The
two are constantly confused, and the confusion is why teams reach for a global `IsDeleted` when what
they actually needed was a status.

## Decision

We will ship **neither an audit trail nor soft delete**, and we will ship the ingredients for both.

`AuditedEntity` stays as it is and keeps its name, documented as record-level provenance. The
`Item` sample keeps its genuine hard delete, because a boilerplate's sample slice should demonstrate
the simple thing and let the project add the complicated one deliberately.

### When you need an audit trail

Everything below already exists in this repository; none of it needs inventing.

**Domain** — add an `AuditEntry` aggregate in `AjBoilerplate.Domain/`, modelled append-only the way
the standards require: private constructor, static factory, **no mutating methods and no public
setters at all**. Append-only is a modelling property before it is a database permission. Populate it
from `Actor` (`Id`, `Name`, `Role`, and optionally `Email`/`Groups`) — that record is the main
ingredient and it is already there. Use `PrivacyHash.OfOrNull` for IP address and user agent; it was
written for exactly this and is currently unused.

**Application** — write the entry **in the use case, inside the same `SaveChangesAsync` as the
change**, not in an interceptor. `ICurrentActor`, `IClock`, and `ICorrelationContext` are all
injectable there already, and `middleware.md` §13 is explicit that the audit boundary is the
Application/domain boundary, "where business context exists — not as raw HTTP middleware". The
use case knows the action was "item archived by the owner"; the persistence layer only knows a
`Status` column changed.

**Infrastructure** — a table whose repository port exposes `AddAsync` and reads, and **no** `Remove`
and no update path. Index on `(EntityType, EntityId, OccurredAt)` and on the actor id. Then make it
real in the database: the application's login should hold `INSERT` and `SELECT` on that table and
not `UPDATE` or `DELETE`. A C# class with no setters is a convention; a revoked grant is a control.

**If it must survive a hard delete** — do not put a foreign key from the audit row to the audited
entity. Store the id as a plain value, exactly as `FeatureAcknowledgement.UserId` deliberately holds
an IdP subject with no FK behind it. A foreign key plus a cascade is how audit history gets deleted
by accident, which is what `efcore-migrations.md` means by "no implicit cascade deletes that could
destroy audit history".

**If it must leave this service** (SIEM, warehouse, an append-only store you do not control) — the
transactional outbox is the right seam and it is already built: `OutboxMessage` written in the same
transaction as the change, `OutboxDispatcher` draining a batch of 50 on a 15-second timer from
`OutboxDispatcherHostedService`, and `IIntegrationEventPublisher` as the swap point where
`LoggingIntegrationEventPublisher` gets replaced by a real transport. Two honest caveats. First,
**nothing in this repository currently produces an outbox row** — `OutboxMessage.Create` has no
caller, so yours would be the first, and the consumer side is a logging no-op until you replace it.
Second, **the outbox is not itself the audit store**: its rows are mutable by design
(`MarkDispatched`, `MarkFailed`, `ResetForRetry`) and they are drained, not retained. Use it to
*deliver* audit records reliably; keep the record itself in your own append-only table.

**Decide retention and immutability before you write the schema.** Those two answers constrain the
storage choice far more than any of the code above.

### When you need soft delete

**First, check that you do not actually need a domain state.** If the answer to "what does a deleted
one behave like?" is anything other than "it does not exist", you want `Archive()`/`Retire()`, and
`Item` and `FeatureAnnouncement` are two worked examples of that in this repository. Take that route
whenever it fits — it is cheaper and it carries meaning.

If the need is genuinely infrastructural — regulatory undelete, recovery from operator error —
then:

- `ISoftDeletable` in `AjBoilerplate.Domain/Common/`, next to `AuditedEntity`.
- Apply the filter in `AppDbContext.OnModelCreating` by **iterating the model's entity types** and
  adding it to every one implementing the interface. Never per-entity by hand: the failure mode is a
  new aggregate that nobody remembered to filter, and a loop cannot forget.
- Add an architecture test in `AjBoilerplate.ArchitectureTests` asserting that every `ISoftDeletable`
  type in the model has a query filter. That project already enforces layering and controller
  conventions; this is the same kind of rule and it is the only thing that will still be true in a
  year.
- Convert every unique index over soft-deletable data to a filtered index (`HasFilter`), starting
  with `IX_feat_Acknowledgements_User_Feature` if that entity is in scope.
- Replace `DeleteBehavior.Cascade` with an explicit cascade in the domain, and delete the
  configuration line rather than leaving it there to be believed.
- Treat every `IgnoreQueryFilters()` call as a review checkpoint, and grep for it before each
  release.
- Re-check the delete endpoint's status codes against ADR-0005 once "deleted" is a state rather than
  an absence.

## Consequences

### Positive

- No project inherits an audit schema that does not match its obligations, and no project has to
  unpick one before it can build the audit trail it actually needs.
- Nothing in the repository can be mistaken for compliance. The absence is total, so it is visible;
  a half-built trail would not have been.
- Every query in the codebase means what it says. There is no filter to remember, no
  `IgnoreQueryFilters()` to audit, no filtered index, and no cascade that has quietly stopped
  cascading.
- The ingredients that *are* generic — `Actor`, `ICurrentActor`, `ICorrelationContext`,
  `PrivacyHash`, `IClock`, the outbox — are all present, so the work a consuming project does is
  assembly, not invention.
- `Item.Archive()` and `FeatureAnnouncement.Retire()` stand as the demonstrated pattern for
  retirement, which is what most teams reaching for soft delete actually wanted.

### Negative

- **Every consuming project that needs an audit trail pays for it separately, and they will all
  differ.** Two teams in the same organisation will produce two schemas and two retention policies
  for what an outsider would call the same feature. That duplication is the direct, unglamorous cost
  of not choosing, and it is real.
- **There is no worked example to copy.** The `Item` slice demonstrates the layered path end to end
  precisely so that a new aggregate has a model to follow — and audit gets no such treatment. The
  "When you need an audit trail" section above is prose, not code, and prose is a weaker teacher.
  The first project to build one will get the boundary wrong at least once.
- **`AuditedEntity` is a naming trap.** It is called `AuditedEntity`, every persisted root inherits
  it, and it records no actor and no history whatsoever. A team can reasonably read the class list,
  see that name, and conclude the auditing question is answered — which is the exact failure this
  ADR exists to prevent, produced by the very code it is defending. The class comment describes what
  it does, and this ADR says what it is not, and neither of those stops someone who only ever reads
  the type name. Renaming it (`TimestampedEntity`, `TrackedEntity`) would fix the trap and cost every
  consuming project a rename in every aggregate; the name stayed, so this cost stays with it.
- **Several doc comments reference audit entries that do not exist** — `ICorrelationContext`,
  `PrivacyHash`, and `docs/architecture.md` §Correlation ids all mention audit records as though they
  were nearby. They are accurate about the ingredients' *purpose* and misleading about the
  repository's *contents*, and this ADR is the only thing reconciling them.
- The shipped standards demand an append-only audit log and the shipped code does not provide one, so
  a security review of a fresh clone will legitimately flag the gap. That is the correct outcome —
  the finding belongs to the consuming project — but somebody has to answer it every time.

### Neutral

- The `Item` sample's `DELETE` is a genuine hard delete, and that is now a documented choice rather
  than an unexamined default.
- Two ideas that are routinely conflated now have names in this repository: *domain-meaningful
  retirement* (`Archive`, `Retire`) and *infrastructural soft delete* (absent). Reviewers can ask
  which one a proposal is.
- The transactional outbox has a documented second use case beyond integration events, without any
  change to its code.
- `PrivacyHash` remains unused in the shipped code. It is a deliberate ingredient, not dead code —
  but anyone pruning unused types should read this ADR first.

### Follow-on work

- If a consuming project builds an audit trail on the outbox, feed back what the seam was missing —
  that is the most likely reason to change `IIntegrationEventPublisher`.
- If a second or third project writes a near-identical audit implementation, that similarity is the
  evidence this decision was wrong, and the answer is a superseding ADR plus a real module, not a
  quiet addition.

## Alternatives considered

### Ship a generic audit-log table plus an EF `SaveChanges` interceptor

The conventional answer, and the cheapest to build: one table, one interceptor, every tracked change
recorded automatically with no per-use-case work at all.

Rejected because an interceptor sees the **EF change graph, not the business action**. It knows that
`Status` went from `Draft` to `Archived` and that `UpdatedAt` moved; it does not know that an owner
archived an item, and "prior → new values" in the standards means the second thing. It also cannot
see events that touch no row — a denied authorisation, a confidential read, an export — which are
usually the first things an auditor asks for. And because it captures everything indiscriminately,
it will faithfully persist fields that must never be stored, with no allow-list and no one having
decided. `middleware.md` §13 already places the audit boundary at the Application/domain layer for
these reasons; an interceptor puts it in Infrastructure, where the business context does not exist.
The trade-off that lost: it buys automatic coverage at the price of recording the wrong thing, and
recording the wrong thing is what makes a half-built audit trail dangerous rather than merely
incomplete.

### Ship soft delete via an `ISoftDeletable` interface and an EF global query filter

Also conventional, and it would have been about thirty lines.

Rejected on cost distribution. A global filter is a permanent tax on every query, every unique
index, every cascade, and every raw-SQL path in **every** consuming project — including the majority
that will never undelete anything. Worse, it would have arrived as an unconditional default, so
projects would inherit the leak risk without ever having decided to accept it, and the first
`IgnoreQueryFilters()` written under deadline pressure would go unreviewed because the pattern was
already blessed. The two entities in this repository that need retirement solve it better in the
domain, with named states and enforced invariants; a generic flag would have shipped a weaker,
meaning-free duplicate of a pattern that is already demonstrated properly. The trade-off that lost:
it buys convenience for the few at a correctness risk borne by all.

### Ship the audit trail as an opt-in module behind a feature flag

Tempting middle ground: the code is there, disabled, and a project switches it on.

Rejected because it does not avoid the original problem, it hides it. The schema, retention, and
immutability guarantees would still have been chosen by us, in advance, for a compliance context we
cannot see — and a flag makes the choice feel already made, so nobody re-opens it. An off-by-default
module also rots: unexercised by any shipped path, its migration and its tests would drift out of
step with the model within a couple of releases. The trade-off that lost: it buys the appearance of
optionality while still pre-deciding every question that actually matters.

### Doing nothing — leave the omission unrecorded

The genuine default: no ADR, just two features that are not there.

Rejected because this particular omission is invisible and actively misleading. A reader encounters
`AuditedEntity` on every aggregate, doc comments on `ICorrelationContext` and `PrivacyHash` that
speak of audit rows, an architecture doc that lists "an audit entry" among the things a correlation
id ties together, and a standards set that mandates an append-only audit log — and finds no such
table. The two available conclusions are both wrong: that audit was forgotten, or that
`AuditedEntity` is it. The second is worse, and without this ADR nothing in the repository
contradicts it. An omission that reads as an oversight needs a record more than a feature does.

## Verification

The decision is being honoured while all of the following hold.

- **Soft delete stays absent.**
  `grep -rn "IsDeleted\|ISoftDeletable\|HasQueryFilter\|IgnoreQueryFilters" src/backend --include='*.cs'`
  returns nothing. Any hit means the boilerplate has acquired an infrastructural soft-delete default
  and this ADR needs superseding — not amending.
- **The audit trail stays absent.** `AppDbContext` maps exactly six `DbSet`s and none is an audit
  table; the model snapshot lists `Items`, `feat_Features`, `feat_Acknowledgements`,
  `OutboxMessages`, `InboxMessages`, `IdempotencyRecords`. A seventh table named for auditing is the
  signal.
- **`AuditedEntity` gains no actor.** A `CreatedBy`/`ModifiedBy` column appearing on the base class
  is this decision being reversed by increment, and it is the change most likely to happen without
  anyone noticing it is one. The class comment and this ADR must be updated together or not at all.
- **The doc comments stay honest.** `ICurrentActor`, `ICorrelationContext`, and `PrivacyHash` may
  describe what they are *for*; if any of them is ever reworded to claim the boilerplate *records*
  audit entries, that is a defect in the comment, not evidence of a feature.
- **The signal to supersede** is two or more consuming projects independently producing near-identical
  audit implementations. At that point the shape has demonstrably stopped being project-specific, the
  central premise of this ADR is false, and the answer is a new ADR plus a real module — never a
  quiet addition to this one.

## References

- `src/backend/src/AjBoilerplate.Domain/Common/AuditedEntity.cs` — record-level provenance, and the
  class this ADR most exists to disambiguate
- `src/backend/src/AjBoilerplate.Domain/Common/Actor.cs` ·
  `src/backend/src/AjBoilerplate.Application/Abstractions/ICurrentActor.cs` — the actor, already
  resolvable
- `src/backend/src/AjBoilerplate.Domain/Common/PrivacyHash.cs` ·
  `src/backend/src/AjBoilerplate.Application/Abstractions/ICorrelationContext.cs` — the two remaining
  ingredients
- `src/backend/src/AjBoilerplate.Domain/Messaging/OutboxMessage.cs` ·
  `src/backend/src/AjBoilerplate.Application/Messaging/OutboxDispatcher.cs` — the recommended
  delivery seam, and its limits
- `src/backend/src/AjBoilerplate.Domain/Items/Item.cs` ·
  `src/backend/src/AjBoilerplate.Domain/Features/FeatureAnnouncement.cs` — domain-meaningful
  retirement, as opposed to soft delete
- `src/backend/src/AjBoilerplate.Infrastructure/Persistence/Configurations/FeatureAcknowledgementConfiguration.cs`
  — the unique index and the cascade that soft delete would break
- `.claude/standards/security.md` §Audit · `.claude/standards/middleware.md` §13 ·
  `.claude/standards/efcore-migrations.md` §Data lifecycle · `.claude/standards/ef-core.md` ·
  `.claude/standards/observability-tracing.md` §Audit vs telemetry — the requirements this decision
  leaves to the consuming project
- [ADR-0005: A uniform `ApiResponse<T>` envelope and status-code contract](0005-apiresponse-envelope-and-status-code-contract.md)
  — the delete endpoint's `204`/`404` contract that soft delete would change the meaning of
- [docs/architecture.md](../architecture.md) §Correlation ids · §Outbox and inbox
