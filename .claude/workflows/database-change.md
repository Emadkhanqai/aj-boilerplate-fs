# Workflow: Database Change

> **Model routing (do first):** see [`../model-routing.md`](../model-routing.md). Schema change
> and migration authoring → workhorse tier.

Any schema change. EF Core migration-based, always. Extends
[`ef-core-migration.md`](ef-core-migration.md) with the review rules.

## 1. Model the change

Adjust the Domain entities and their `IEntityTypeConfiguration<T>`. Monetary values are
`decimal` with **explicit precision**; timestamps are UTC `datetime2`; strings have explicit
lengths. Add a `rowversion` concurrency token to any aggregate that can be edited
concurrently. See
[`../standards/efcore-migrations.md`](../standards/efcore-migrations.md).

## 2. Create the migration

```bash
cd src/backend
dotnet ef migrations add <BusinessIntentName> \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output-dir Migrations
```

**The name states business intent** — `AddItemArchivedAt`, not `Update1`.

## 3. Review the generated migration

Use the checklist in [`../templates/ef-migration.md`](../templates/ef-migration.md). At
minimum: inspect `Up()`/`Down()` for **data loss**, check index and constraint changes, confirm
precision and lengths, and confirm `Down()` genuinely reverses `Up()`.

**Never edit a migration that has already been applied to a shared environment** — fix forward
with a new one. The [`protect-files`](../hooks/protect-files.sh) hook blocks the edit.

## 4. Produce a deployable script

```bash
dotnet ef migrations script --idempotent \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output database/migrations/<BusinessIntentName>.sql
```

**Never auto-apply migrations on production startup** — the script is applied as a controlled,
reviewed release step ([`release.md`](release.md)).

## 5. Breaking changes: expand → migrate → contract

Add the new shape, backfill and dual-write, cut over, and only then drop the old shape in a
*later* release. A single migration that renames a column in place will take the running
application down.

## 6. Test

Build, then run the unit and architecture tests, then the **integration tests against a real
database** — they apply the migrations and will catch what review missed. Use a transaction for
any multi-step operation so partial state is never persisted.

## 7. Review & gate

[`/review`](../commands/review.md), then
[`pre-push-quality-gate.md`](pre-push-quality-gate.md). **No push without explicit approval.**
