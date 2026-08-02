# Standard: Entity Framework Core

**Provider:** Microsoft SQL Server. **Approach:** migration-based, always.

## Hard rules

1. **Every schema change is an EF Core migration.** No exceptions.
2. **Never `EnsureCreated()`** in any environment — it bypasses migrations permanently.
3. **No manual or out-of-band DDL** against any shared database. Schema is defined by
   migrations and only by migrations.
4. **One provider in production code paths** (`Microsoft.EntityFrameworkCore.SqlServer`). An
   in-memory or SQLite provider may appear *only* inside isolated tests, never in a shipped
   path.
5. **Never auto-apply migrations on production startup.** Applying schema is a controlled
   release step (see [`efcore-migrations.md`](efcore-migrations.md)).

## Where things live

- The `DbContext` (`AppDbContext`), entity configurations, and `Migrations/` live in
  `AjBoilerplate.Infrastructure`.
- Entities live in `AjBoilerplate.Domain` and stay persistence-ignorant (no EF Core
  attributes). Mapping is done with `IEntityTypeConfiguration<T>` (Fluent API) in
  Infrastructure.

## Migration workflow

Run from `src/backend/`:

```bash
# Create a migration
dotnet ef migrations add <Name> \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output-dir Migrations

# Apply to the local database
dotnet ef database update \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api

# Produce an idempotent SQL script for review / deployment
dotnet ef migrations script --idempotent \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output database/migrations/<Name>.sql
```

The step-by-step is the [`/new-migration`](../commands/new-migration.md) command.

## Conventions

- Migration names are descriptive and PascalCase and state business intent — `AddItemStatus`,
  not `Update1`.
- **Review every generated migration by hand before applying.** EF Core's inference can drop
  columns or rebuild tables.
- Keep the model snapshot in source control; commit migration + snapshot + SQL script
  together.
- Use `decimal(18,4)` for monetary values; `datetime2` for timestamps; explicit max lengths on
  strings. Configure precision in Fluent configuration — never rely on defaults.
- Seed reference/lookup data through a migration or a dedicated **idempotent** seeder, never
  ad-hoc inserts.
- Concurrency: `rowversion` on any entity that supports concurrent edits.
- Append-only tables (audit) are modelled so entries can only be inserted — never updated or
  deleted.

## Related

[`mssql.md`](mssql.md) · [`efcore-migrations.md`](efcore-migrations.md) · [`clean-architecture.md`](clean-architecture.md) · [`../commands/new-migration.md`](../commands/new-migration.md) · [`../workflows/ef-core-migration.md`](../workflows/ef-core-migration.md) · [`../templates/ef-migration.md`](../templates/ef-migration.md)
