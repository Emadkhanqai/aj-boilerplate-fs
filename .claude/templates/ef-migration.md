# Template: EF Core Migration checklist

Use with [`../workflows/ef-core-migration.md`](../workflows/ef-core-migration.md) and
[`/new-migration`](../commands/new-migration.md).

```bash
cd src/backend
dotnet ef migrations add <PascalCaseName> \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output-dir Migrations
```

## Before applying, verify the generated `Up()` / `Down()`

- [ ] **No unintended column drops or table rebuilds.** A type change can silently rebuild a
      table and lose data.
- [ ] Monetary values → `decimal(18,4)`; percentages → `decimal(9,4)`. **Precision is
      explicit**, never defaulted.
- [ ] Timestamps → `datetime2`, stored **UTC**.
- [ ] Strings → `nvarchar` with an **explicit max length**. `nvarchar(max)` only where
      justified.
- [ ] Unique index on every real business key (filtered, where the column is nullable until
      assigned).
- [ ] Indexes on the columns actually filtered, sorted, and joined on — and only those.
- [ ] `rowversion` concurrency token on any aggregate that can be edited concurrently.
- [ ] **No cascade delete** that could remove audit history.
- [ ] Append-only tables gain no update or delete path.
- [ ] `Down()` cleanly and completely reverses `Up()`.
- [ ] The migration name states **business intent**, not `Update1`.

## Then

```bash
dotnet ef database update \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api

dotnet ef migrations script --idempotent \
  --project src/AjBoilerplate.Infrastructure \
  --startup-project src/AjBoilerplate.Api \
  --output database/migrations/<PascalCaseName>.sql
```

Run the integration tests — they apply migrations against a real database and catch what
review missed. Commit the migration, the model snapshot, and the SQL script **together**.

---

Migration-based · no `EnsureCreated` · no manual DDL · never auto-apply on production startup ·
**an applied migration is immutable — fix forward.**
