# Workflow: EF Core Migration

> **Model routing (do first):** see [`../model-routing.md`](../model-routing.md). Migration
> authoring → workhorse tier.

Every schema change ships as a migration. **No `EnsureCreated`. No manual DDL.**

The slash-command form of this workflow is
[`/new-migration`](../commands/new-migration.md).

## 1. Change the model

Edit the Domain entities and their `IEntityTypeConfiguration<T>` in Infrastructure. The
migration is generated *from* the model — never hand-authored to lead it.

## 2. Add the migration

```bash
cd src/backend
dotnet ef migrations add <PascalCaseName> \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output-dir Migrations
```

## 3. Review the generated migration by hand

Work through [`../templates/ef-migration.md`](../templates/ef-migration.md). Confirm there are
no unintended column drops or table rebuilds, that precision and lengths are explicit, and
that unique indexes exist for real business keys.

## 4. Apply locally

```bash
dotnet ef database update \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api
```

## 5. Produce a reviewable SQL script

```bash
dotnet ef migrations script --idempotent \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output database/migrations/<PascalCaseName>.sql
```

## 6. Commit

The migration code, the model snapshot, and the generated SQL script go together, on a branch.
They are one change. **Do not push without explicit approval and a green quality gate.**

## Notes

- Seed reference data through an **idempotent** seeder, not ad-hoc SQL.
- Append-only tables (audit) must stay append-only — never author a migration that opens an
  update or delete path on one without an explicit, approved reason.
- An applied migration is immutable. Fix forward.
