# "What's new" feature announcements

A server-driven way to spotlight a newly shipped feature to each user **exactly once**, the first
time they land on a page the announcement is bound to.

The backend owns the decision — which announcements are live, which routes they apply to, and who
has already dismissed which — so a client only has to ask "anything to show here?" and report back
"they closed it".

**Contents**

- [How it behaves](#how-it-behaves)
- [The API](#the-api)
- [Data model](#data-model)
- [Where the code lives](#where-the-code-lives)
- [The client side](#the-client-side)
- [Adding an announcement (the day-2 workflow)](#adding-an-announcement-the-day-2-workflow)
- [Retiring an announcement](#retiring-an-announcement)
- [The two non-obvious correctness details](#the-two-non-obvious-correctness-details)
- [What is deliberately not here](#what-is-deliberately-not-here)

---

## How it behaves

1. An announcement row describes one feature: a title and body (English and Arabic), and a list of
   URL path prefixes it may appear on — or an empty list, meaning "every route".
2. On each route change the client asks `GET /api/v1/features/unack?path=<current path>`. The API
   answers with the active announcements **this user** has not dismissed whose page list matches
   that path.
3. When the user closes whatever the client renders, it posts every id it showed to
   `POST /api/v1/features/ack`. Those announcements never surface for that user again.

Acknowledgement is stored **server-side, per user** rather than in the browser. That is the point:
dismissing on a laptop must not leave the announcement re-appearing on a phone, clearing site data
must not resurrect it, and "how many people have seen this?" stays an answerable question because
each dismissal is a real row.

---

## The API

Both endpoints require a signed-in caller and nothing more — `Policies.ReadAccess`, this project's
widest policy, satisfied by every recognised role.

### `GET /api/v1/features/unack?path=/reports/monthly`

Announcements to show, ordered by `DisplayOrder` then `CreatedAt` — with `Id` as a final tiebreak,
so two announcements written in the same tick with the same order still come back in a stable
sequence. An empty array means "nothing to show", and it is the normal answer.

```jsonc
{
  "success": true,
  "data": [
    {
      "id": "0f8c…",
      "key": "search-v2",          // stable handle, never displayed
      "titleEn": "Faster search",
      "titleAr": null,             // null → fall back to the English field
      "bodyEn": "Results now appear as you type.",
      "bodyAr": null,
      "displayOrder": 0,
      "createdAt": "2026-08-03T10:05:48+00:00"
    }
  ],
  "statusCode": 200,
  "traceId": "…"
}
```

`path` is treated as a path and nothing else: any query string or fragment is discarded, and `.` /
`..` segments are resolved, **before** it is compared against a page list. Omitting it means `/`.

### `POST /api/v1/features/ack`

```jsonc
{ "featureIds": ["0f8c…", "1a2b…"] }
```

Answers **204 No Content** with no body. Idempotent: ids this user already acknowledged are skipped,
and an id naming no announcement is ignored rather than failing the batch. An empty array is an
accepted no-op. Send every id that was shown in one request, not one request per id.

---

## Data model

Two tables. Neither is written by an API endpoint except `feat_Acknowledgements`, one row at a time,
on behalf of the caller.

### `feat_Features` — one row per announcement

| Column | Type | Notes |
|---|---|---|
| `Id` | `uniqueidentifier` (PK) | Assigned by the application, not the database. |
| `Key` | `nvarchar(80)` | **Unique.** Stable machine handle, e.g. `search-v2`. Never displayed. |
| `TitleEn` | `nvarchar(200)` | Required. |
| `TitleAr` | `nvarchar(200)` | Null falls back to `TitleEn`. |
| `BodyEn` | `nvarchar(2000)` | Required. |
| `BodyAr` | `nvarchar(2000)` | Null falls back to `BodyEn`. |
| `PagesJson` | `nvarchar(1000)` | JSON array of path prefixes, e.g. `["/reports","/settings"]`. `[]` = every route. |
| `IsActive` | `bit` | Soft on/off. False retires it without losing history. |
| `DisplayOrder` | `int` | Lower shows first; ties break by `CreatedAt`. |
| `CreatedAt` / `UpdatedAt` | `datetimeoffset(7)` | From `AuditedEntity`. |
| `RowVersion` | `rowversion` | From `AuditedEntity`. |

Indexes: unique on `Key`; `(IsActive, DisplayOrder)` covering the hot-path lookup.

### `feat_Acknowledgements` — one row per (user, announcement) dismissal

| Column | Type | Notes |
|---|---|---|
| `Id` | `uniqueidentifier` (PK) | |
| `UserId` | `nvarchar(128)` | The identity provider's subject identifier, verbatim. **No foreign key** — users live in the IdP, not in this schema. |
| `FeatureId` | `uniqueidentifier` | FK → `feat_Features.Id`, **ON DELETE CASCADE**. |
| `AcknowledgedAt` | `datetimeoffset(7)` | |
| `CreatedAt` / `UpdatedAt` / `RowVersion` | | From `AuditedEntity`. |

Indexes: **unique on `(UserId, FeatureId)`** — this is a correctness constraint, not tuning; plus a
non-unique index on `FeatureId` for the cascade.

---

## Where the code lives

| Layer | File | Responsibility |
|---|---|---|
| Domain | `Features/FeaturePath.cs` | Canonicalises a request path. Security control — see below. |
| Domain | `Features/FeatureAnnouncement.cs` | The aggregate, plus `Targets(path)` — "do I apply to this route?" |
| Domain | `Features/FeatureAcknowledgement.cs` | One user's dismissal. |
| Application | `Features/FeatureAnnouncementService.cs` | The two use cases. |
| Application | `Features/IFeatureAnnouncementRepository.cs` | Persistence port. |
| Application | `Features/FeatureValidators.cs` | FluentValidation on both requests. |
| Contracts | `Features/FeatureContracts.cs` | The wire DTOs. |
| Infrastructure | `Persistence/FeatureAnnouncementRepository.cs` | EF Core implementation. |
| Infrastructure | `Persistence/Configurations/Feature*Configuration.cs` | MSSQL mapping and indexes. |
| Api | `Controllers/FeaturesController.cs` | Routing, authorization, DTO mapping. |

The Application layer returns its own `FeatureAnnouncementDto`; the Api maps it to
`FeatureAnnouncementResponse`. That is not ceremony — the architecture tests forbid Application from
referencing Contracts, so a breaking wire change cannot ripple into the use cases.

Covered by `FeaturePathTests`, `FeatureAnnouncementTests`, and `FeatureAnnouncementServiceTests` in
`AjBoilerplate.UnitTests`, and end to end by `Api/FeaturesApiTests.cs` in
`AjBoilerplate.IntegrationTests`.

---

## The client side

Nothing above assumes a particular client — the API is the whole contract. This repository's Angular
app is one implementation of it, in four pieces, none of which live in a feature library:

| Piece | Path (under `src/frontend/`) | Responsibility |
|---|---|---|
| Gateway | `libs/data-access/api-client/src/lib/feature-announcements-api.service.ts` | `unack(path)` and `ack(ids)`. A null envelope payload becomes `[]`, so no caller writes a null check. |
| Modal | `libs/shared/ui/src/lib/whats-new-modal/` | The carousel: hero band, benefit cards, pagination dots. |
| Language | `libs/shared/util/src/lib/language.service.ts` | `pick(en, ar)` — chooses a paired field for the current locale, falling back rather than rendering a blank. |
| Wiring | `libs/shell/src/lib/app-layout/app-layout.ts` | Calls `unack` on every `NavigationEnd`; posts `ack` on dismiss. |

**Why the shell.** The lookup has to run on every route change whatever page is mounted, and one
announcement can be scoped to several unrelated pages. Mounting it once in the authenticated layout
means every page is covered with no per-page work, and no feature library knows the module exists.

**Body conventions.** The server sends plain text and never interprets it. This client chooses to
read a light markdown, which is a client decision another client is free to ignore:

```text
Set up a list the way you like it once, then jump back to it from the sidebar.

- 🔖 Saved views — pin any combination of filters and sorting under a name you choose
- ⚡ Instant search — results narrow as you type
```

A line starting `- ` becomes a tinted benefit card; a leading emoji becomes its icon, and the first
spaced em dash (` — `) splits the card title from its description. Every other non-empty line
becomes a centred paragraph. Blank lines separate and render nothing.

**Two client behaviours that look like bugs.**

1. **The backdrop is inert.** `onBackdrop()` is an empty method with a comment saying so.
   Acknowledgement is permanent and applies on every device, so it may only be written on an
   explicit "Got it" or close — a stray click outside the panel is not "I have read this".
2. **The pending list is never cleared on success.** The lookup only ever *sets* a non-empty
   result. A fast double-navigation can land a newer, empty response while the modal is open, and
   clearing on it would blink an open modal away mid-read. The list is cleared in exactly one
   place — `onWhatsNewClosed`, on a deliberate dismiss — which clears first and *then* POSTs, so a
   failed `ack` still leaves the popup closed and the server simply re-offers it later.

Both the lookup and the acknowledgement fail silently. A feature popup is non-critical UX; a toast
about it would be worse than the failure.

**Offline demo.** `apps/web/src/mocks/handlers.ts` answers both endpoints with one sample
announcement, so `npx nx serve web --configuration=demo` shows the spotlight with no backend and no
database. That sample is a **browser mock, not seed data** — see
[What is deliberately not here](#what-is-deliberately-not-here). Its acknowledgement set is
in-memory, so a reload replays the announcement, which is what makes the demo deterministic.

The modal is the codebase's one sanctioned exception to the PrimeNG-only rule: bespoke markup and a
bespoke stylesheet, with only its icons from PrimeIcons. The reasoning and the costs are in
[ADR-0007](adr/0007-bespoke-whats-new-modal.md).

---

## Adding an announcement (the day-2 workflow)

Announcements are **content, not user input**: there is no create endpoint, no admin screen, and no
seed data in the schema migration. Shipping one is a data-only change that goes through the same
review and the same deployment as any other schema change.

Generate an empty migration and put a single `INSERT` in its `Up`:

```bash
dotnet ef migrations add AddSearchV2Announcement \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output-dir Persistence/Migrations
```

```csharp
protected override void Up(MigrationBuilder migrationBuilder) =>
    migrationBuilder.Sql("""
        INSERT INTO feat_Features
            (Id, [Key], TitleEn, TitleAr, BodyEn, BodyAr, PagesJson, IsActive, DisplayOrder, CreatedAt)
        VALUES
            (NEWID(), 'search-v2', 'Faster search', NULL,
             'Results now appear as you type.', NULL,
             '["/reports","/settings"]', 1, 0, SYSUTCDATETIME());
        """);

protected override void Down(MigrationBuilder migrationBuilder) =>
    migrationBuilder.Sql("DELETE FROM feat_Features WHERE [Key] = 'search-v2';");
```

Notes:

- **Give `Down` the matching `DELETE`.** A migration that cannot be rolled back is not finished, and
  the cascade removes the acknowledgements with it.
- **Pick a new `Key`.** It is unique, so a copy-pasted one fails the migration rather than silently
  shipping a duplicate — which is the intent.
- **Set `DisplayOrder` above the highest existing value** unless you specifically want the new
  announcement to interrupt the order of ones already pending.
- `PagesJson` must be a JSON array of strings. An unreadable value degrades to "matches every route"
  rather than throwing, so a typo here is loud in behaviour but never an outage.
- No code changes anywhere — that is the whole point of the design.

---

## Retiring an announcement

Set `IsActive = 0`. The row and every acknowledgement of it survive, so the history stays
queryable and the announcement simply stops being returned:

```sql
UPDATE feat_Features SET IsActive = 0, UpdatedAt = SYSUTCDATETIME() WHERE [Key] = 'search-v2';
```

Delete the row only when you want the history gone too — the cascade will take the acknowledgements
with it.

---

## The two non-obvious correctness details

Do not "simplify" either of these. Two more live on the client, for the same reason — see
[The client side](#the-client-side).

### 1. The path-traversal guard in `FeaturePath.Normalize`

Page matching is a prefix comparison, and a prefix comparison against an **unresolved** path is
exploitable. A caller sending `path=/reports/../admin` produces a string that literally starts with
`/reports` — so an announcement scoped to the reports area would fire on what actually resolves, in
the browser and everywhere else, to `/admin`.

`Normalize` therefore resolves `.` and `..` segments on a stack (and drops the query string and
fragment) before any comparison happens, and `..` can never walk above the root. `Targets` applies it
itself rather than trusting a caller to have done so; normalisation is idempotent, so applying it
twice costs nothing.

Covered by `FeaturePathTests` and `FeatureAnnouncementTests`, and end to end in `FeaturesApiTests`.

### 2. Idempotency is computed in application code, not caught from the database

`AcknowledgeAsync` reads which of the requested ids this user has already acknowledged and inserts
only the remainder. A double-click or a retried request therefore writes nothing and returns 204 —
rather than raising a unique-constraint violation that would have to be caught, classified by SQL
error number, and translated back into the success it always was.

The unique index on `(UserId, FeatureId)` stays as the authoritative backstop for the one case the
application check cannot cover: two requests racing past it at the same instant. That is a real
constraint doing real work, not a duplicate of the application check.

---

## What is deliberately not here

- **No seed row.** `20260803100548_AddFeatureAnnouncements` creates `feat_Features`,
  `feat_Acknowledgements`, and their four indexes — and inserts nothing. An empty announcements
  table is the correct state for a fresh clone, and the module is simply silent until a later
  migration adds a row. The sample announcement in `apps/web/src/mocks/handlers.ts` is a **browser
  mock for the offline demo build, not seed data**: it never reaches a database, and deleting it
  changes nothing about a real deployment. Do not conflate the two — a "sample" row in a schema
  migration is exactly the thing this design avoids, because it would appear in every environment
  the migration is applied to, including production.
- **No write API.** Announcements arrive by migration. Adding a create endpoint would mean building
  an authoring UI, an approval path, and an audit trail for what is a handful of rows a year.
- **No per-browser storage.** See [How it behaves](#how-it-behaves).
- **No rendering opinion.** The API returns text and lets the client decide how to present it. The
  body is plain text; a client may adopt its own light-markup conventions, and the server will not
  interpret them either way. The ones this repository's Angular app uses are in
  [The client side](#the-client-side).
