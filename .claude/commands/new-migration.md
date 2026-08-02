---
description: Create, review, apply, and script an EF Core migration.
---

# /new-migration `<PascalCaseName>`

Create an EF Core migration following [`../standards/ef-core.md`](../standards/ef-core.md) and
[`../standards/efcore-migrations.md`](../standards/efcore-migrations.md).

## Steps

1. Confirm the Domain model and `IEntityTypeConfiguration` changes are in place first. The
   migration is generated *from* the model, never hand-authored to lead it.
2. Choose a name that states business intent — `AddItemArchivedAt`, not `Update1`.
3. Add the migration, from `src/backend/`:
   ```bash
   dotnet ef migrations add <PascalCaseName> \
     --project src/AjBoilerplate.Infrastructure \
     --startup-project src/AjBoilerplate.Api \
     --output-dir Migrations
   ```
4. **Review the generated `Up()` and `Down()` by hand**, using the full checklist in
   [`../templates/ef-migration.md`](../templates/ef-migration.md). Check for:
   - unintended `DropColumn` / `DropTable` (data loss)
   - a column type change that forces a table rebuild
   - index and constraint changes you did not ask for
   - a `Down()` that does not actually reverse `Up()`
   - explicit precision on every `decimal`, explicit length on every string
   - the concurrency token where the entity supports concurrent edits
5. Apply locally:
   ```bash
   dotnet ef database update \
     --project src/AjBoilerplate.Infrastructure \
     --startup-project src/AjBoilerplate.Api
   ```
6. Script it for review and deployment:
   ```bash
   dotnet ef migrations script --idempotent \
     --project src/AjBoilerplate.Infrastructure \
     --startup-project src/AjBoilerplate.Api \
     --output database/migrations/<PascalCaseName>.sql
   ```
7. Run the integration tests — they apply migrations against a real database and will catch
   what review missed.
8. **Commit the migration, the model snapshot, and the SQL script together.** They are one
   change. **Do not push without approval.**

## Rules

- Never `EnsureCreated`. Never manual DDL. Never auto-apply on production startup.
- The full procedure is [`../workflows/ef-core-migration.md`](../workflows/ef-core-migration.md);
  the wider schema-change flow is
  [`../workflows/database-change.md`](../workflows/database-change.md).
- **Never edit a migration that has already been applied to a shared environment** — fix
  forward with a new one. The `protect-files` hook blocks the edit for you.
- Breaking changes use expand → migrate → contract across releases.
