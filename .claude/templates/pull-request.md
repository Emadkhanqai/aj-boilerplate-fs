# PR: <concise title>

## Summary

What changed and why. Link the spec in `docs/specs/` and any ADR in `docs/adr/`.

## Changes

- **Backend:** <layers touched, migrations added>
- **Frontend:** <libraries/routes touched, types regenerated?>
- **Docs / infra:** <OpenAPI snapshot, ADRs, IaC>

## Quality gate (green before requesting merge)

- [ ] `dotnet build --no-incremental` clean (warnings are errors)
- [ ] `dotnet format --verify-no-changes` clean
- [ ] `dotnet test` green (unit + integration + architecture)
- [ ] `npx nx run-many -t lint typecheck test build` clean
- [ ] Playwright journeys green; axe-core clean on new or changed screens
- [ ] SonarQube — **0** Blocker / Critical / Major, coverage on new code ≥80%
- [ ] Gitleaks clean
- [ ] Schema change shipped as a reviewed migration (if applicable)

## Architecture & standards

- [ ] Clean Architecture boundaries respected
- [ ] Nx module boundaries respected
- [ ] Standalone + OnPush + signals + `inject()`; typed reactive forms; PrimeNG only
- [ ] No hand-written HTTP client, no hand-duplicated DTO
- [ ] No `any`; no `EnsureCreated`; no manual DDL

## API contract

- [ ] Versioned route; `ApiResponse<T>` envelope with `traceId`
- [ ] Correct status codes, including the hide-as-404 / 409-concurrency / 410-vs-404 calls
- [ ] OpenAPI documented; `docs/api/` snapshot refreshed; frontend types regenerated

## Security

- [ ] Deny-by-default policy on every endpoint
- [ ] Object ownership validated after loading the resource
- [ ] Restricted fields removed by DTO projection, with a test proving absence from the payload
- [ ] No secrets, real hostnames, project ids, or credentials

## Notes / risks

<remaining risks, follow-ups, anything a reviewer should look at hardest>

> Push requires explicit approval, every time. The SonarQube gate must pass first.
