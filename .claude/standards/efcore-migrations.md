# Standard: EF Core Migrations & Data Modelling

Schema evolves **only** through EF Core migrations. Complements [`ef-core.md`](ef-core.md) and
[`mssql.md`](mssql.md).

## Migrations

- **EF Core migrations only** for schema evolution. No `EnsureCreated`, no manual out-of-band
  DDL.
- **Every schema change has a migration.** No drift between the model and the database.
- **The migration name describes business intent** — `AddItemArchivedAt`, not `Update1`.
- **Review the generated migration before applying** — inspect `Up()`/`Down()`, data-loss
  operations, index and constraint changes, and column-type changes that force a table
  rebuild.
- **Never auto-apply migrations on production startup.** Apply as a controlled release step
  and generate an idempotent SQL script for review.
- **A migration that has already been applied to a shared environment is immutable.** Fix it
  forward with a new migration; never edit it in place. The `protect-files` hook blocks edits
  to applied migration files for exactly this reason.
- **Expand → migrate → contract** for breaking schema changes: add the new shape, backfill and
  dual-write, cut over, and only then drop the old shape in a later release.

## Precision & types

- **Use `decimal` for monetary values — never `float` or `double`.**
- **Configure decimal precision explicitly** — e.g. `decimal(18,4)`. No implicit or default
  precision.
- Store timestamps in **UTC** as `datetime2` (or `datetimeoffset` where the offset matters).
- Explicit max lengths on every string column; `nvarchar(max)` only when justified.

## Indexes & constraints

- **Add indexes** for the columns you actually filter, sort, and join on — and only those.
- **Unique constraints for real business keys** (a filtered unique index where the column is
  nullable until assigned).
- **Concurrency token (`rowversion`)** on any aggregate that can be edited concurrently; pair
  it with `ETag` / `If-Match` and a `409` response (see [`middleware.md`](middleware.md) and
  [`api-response-format.md`](api-response-format.md)).
- Foreign keys are explicit. No implicit cascade deletes that could destroy audit history.

## Data lifecycle

- **Soft delete only where the business requires it** — not as a blanket pattern. A soft-delete
  flag that every query must remember to filter is a bug generator; use a global query filter
  if you adopt it.
- **Audit tables are append-only** — never updated, never deleted.

## Query performance

- **Lazy loading off.** Use explicit `Include` or, better, projection to the shape you need.
- **No N+1.** Project to a DTO in the query rather than materialising graphs and mapping.
- **`AsNoTracking` for read-only queries.**
- **Paginate every list.** No unbounded result set reaches the API.
- Watch for client-side evaluation; a query that silently falls back to in-memory filtering is
  a performance bug.

## Transactions

- **Use a transaction for multi-step operations** so partial state is never persisted.
- Where a value must be issued exactly once, use an appropriately serialised transaction (and
  a distributed lock once the service scales horizontally).

## Related

[`ef-core.md`](ef-core.md) · [`mssql.md`](mssql.md) · [`dotnet-security.md`](dotnet-security.md) · [`../commands/new-migration.md`](../commands/new-migration.md)
