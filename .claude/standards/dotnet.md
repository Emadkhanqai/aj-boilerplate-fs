# Standard: .NET Backend

**Target:** latest .NET LTS · ASP.NET Core Web API · C# latest.

## Project layout

Solution `src/backend/AjBoilerplate.slnx` with:

- `src/AjBoilerplate.Domain`
- `src/AjBoilerplate.Application`
- `src/AjBoilerplate.Infrastructure`
- `src/AjBoilerplate.Contracts`
- `src/AjBoilerplate.Api`
- `tests/AjBoilerplate.UnitTests`
- `tests/AjBoilerplate.IntegrationTests`
- `tests/AjBoilerplate.ArchitectureTests`

Dependency directions are defined in [`clean-architecture.md`](clean-architecture.md) and
enforced by `AjBoilerplate.ArchitectureTests`.

## Language & style

- `Nullable` **enabled**, `ImplicitUsings` enabled, `TreatWarningsAsErrors` **true**.
- `LangVersion` latest. File-scoped namespaces. One top-level type per file.
- Prefer `record` for immutable DTOs/value objects; `sealed` classes by default.
- Async all the way: `async`/`await`, `CancellationToken` on every I/O path, suffix async
  methods with `Async`.
- No `async void` (except event handlers). No `.Result` / `.Wait()` blocking.
- Use `System.Text.Json` (not Newtonsoft) unless a specific, reviewed need arises.
- Analyzers on: `EnableNETAnalyzers`, `AnalysisLevel latest`, `.editorconfig` committed.

## Validation

- Use **FluentValidation** for request/command validation. Validators live in Application.
  Wire them into the pipeline so failures surface as the standard envelope
  (see [`input-validation-sanitization.md`](input-validation-sanitization.md)).

## Errors

- API errors return the `ApiResponse` envelope — see
  [`api-response-format.md`](api-response-format.md) and
  [`error-handling.md`](error-handling.md).
- Domain/Application throw typed exceptions or return result objects; the
  `IExceptionHandler` chain maps them. No raw 500s leaking stack traces in production.

## Configuration & secrets

- `appsettings.json` for non-secret config; environment-specific overrides via
  `appsettings.{Environment}.json` and environment variables.
- **No secrets in source.** Locally use `dotnet user-secrets`; in the cloud use the
  provider's secret store behind `ISecretsProvider` (see [`cloud.md`](cloud.md)).
- Connection strings are configuration, never literals (see [`mssql.md`](mssql.md)).

## Build & test commands

```bash
dotnet restore
dotnet build --no-incremental          # warnings are errors
dotnet test
dotnet format --verify-no-changes      # style gate
dotnet list package --vulnerable --include-transitive
```

## Related

[`clean-architecture.md`](clean-architecture.md) · [`ef-core.md`](ef-core.md) · [`api-design.md`](api-design.md) · [`testing.md`](testing.md) · [`security.md`](security.md)
