# Standard: Microsoft SQL Server

**MSSQL is the supported relational database** for this boilerplate. Swapping it is an ADR
decision, not a casual change — the migration history, the type conventions below, and the
integration tests all assume it.

## Environments

- **Local dev:** a local SQL Server instance (Developer or Express edition), or a managed
  instance you own. Point at it through configuration only.
- **Cloud:** the provider-appropriate managed SQL Server — see [`cloud.md`](cloud.md).

## Connection strings

- **Always configuration, never a literal in source.** Locally use `dotnet user-secrets`; in
  the cloud use the provider's secret store behind `ISecretsProvider`.
- **Prefer managed/workload identity over a SQL login** wherever the platform supports it.
- Example shape (local development only):
  `Server=localhost;Database=AjBoilerplate;Trusted_Connection=True;TrustServerCertificate=True;`
- Never commit a connection string containing a password. The `secret-scan` hook blocks it.
- `Encrypt=True` in every non-local environment.

## Data type conventions

| Concept | Type |
|---|---|
| Monetary amounts and rates | `decimal(18,4)` |
| Percentages | `decimal(9,4)` |
| Timestamps (UTC) | `datetime2` (or `datetimeoffset` when the offset matters) |
| Surrogate identifiers | `int` / `bigint` IDENTITY, or `uniqueidentifier` |
| Durable business keys | `nvarchar` with an explicit length + unique index |
| Text | `nvarchar` with an explicit max length; `nvarchar(max)` only when justified |
| Concurrency token | `rowversion` |

## Schema conventions

- **All timestamps stored in UTC.** Convert at the edge, never in the database.
- Uniqueness of any durable business key is enforced by a database unique index, not only by
  application code.
- Foreign keys and indexes are explicit; no implicit cascades that could delete audit history.
- **Unicode everywhere** — `nvarchar`, not `varchar` — so any language is storable from day
  one.
- Use a consistent collation across the database; declare it in the initial migration.

## Related

[`ef-core.md`](ef-core.md) · [`efcore-migrations.md`](efcore-migrations.md) · [`cloud.md`](cloud.md) · [`security.md`](security.md)
