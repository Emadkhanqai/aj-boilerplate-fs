# Standard: Clean Architecture (.NET)

**Applies to:** the `AjBoilerplate.*` backend solution under `src/backend/`.

## Layers & dependency rule

Dependencies point **inward only**. Nothing inner knows about anything outer.

```
        ┌──────────────────────────────────────────┐
        │                  Api                      │  (host, DI, controllers, middleware)
        │  depends on → Application, Infrastructure, │
        │               Contracts                    │
        └───────────────────┬──────────────────────┘
                            │
     ┌──────────────────────┴───────────────────────┐
     │              Infrastructure                   │  (EF Core, database, external services)
     │      depends on → Application, Domain          │
     └──────────────────────┬───────────────────────┘
                            │
             ┌──────────────┴──────────────┐
             │          Application         │  (use cases, CQRS handlers, ports)
             │      depends on → Domain      │
             └──────────────┬──────────────┘
                            │
                    ┌───────┴────────┐
                    │     Domain      │  (entities, value objects, domain rules)
                    │ depends on NOTHING│
                    └────────────────┘

   Contracts  (DTOs only, no business logic) — referenced by Api (and consumers)
```

## The rules (enforced by `AjBoilerplate.ArchitectureTests`)

| Project | May depend on | Must NOT depend on |
|---|---|---|
| `AjBoilerplate.Domain` | *(nothing — no EF, no ASP.NET, no external packages beyond base)* | Everything else |
| `AjBoilerplate.Application` | Domain | Infrastructure, Api |
| `AjBoilerplate.Infrastructure` | Application, Domain | Api |
| `AjBoilerplate.Api` | Application, Infrastructure, Contracts | *(is the composition root)* |
| `AjBoilerplate.Contracts` | *(nothing — pure DTOs)* | Domain, Application, Infrastructure |

An architecture test asserts each row. A wrong-direction reference fails the build — it is
never "fixed" by relaxing the test.

## Responsibilities

- **Domain** — entities (the sample ships `Item`), value objects, enums, domain events, and
  invariants. Persistence-ignorant. No attributes from EF Core or ASP.NET Core.
- **Application** — use cases (commands/queries + handlers), orchestration, validation
  (FluentValidation), and **ports** (interfaces such as `IItemRepository`, `IClock`,
  `ISecretsProvider`) that Infrastructure implements.
- **Infrastructure** — EF Core `DbContext`, the database provider, migrations, repository
  implementations, caching, messaging, external gateways, and other adapters.
- **Contracts** — request/response **DTOs only**, plus the `ApiResponse<T>` envelope. Shared
  with the frontend via OpenAPI. No logic, no domain types, no EF types.
- **Api** — thin controllers, DI wiring (composition root), middleware, auth, OpenAPI.
  Maps Contracts ↔ Application.

## Conventions

- Application uses **CQRS-style** handlers. A mediator is acceptable; keep it simple if a
  plain handler suffices.
- Controllers are thin: validate input shape, dispatch to Application, map the result to a
  Contracts DTO, return. **No business logic in controllers.**
- Domain never returns Contracts DTOs; mapping happens in Api/Application.
- Feature folders live *inside* each layer (`Application/Items/`, `Api/Controllers/Items/`),
  so a feature is greppable without breaking the layer rule.

## Related

[`dotnet.md`](dotnet.md) · [`ef-core.md`](ef-core.md) · [`api-design.md`](api-design.md) · [`testing.md`](testing.md) · [`../templates/domain-entity.md`](../templates/domain-entity.md) · [`../templates/api-controller.md`](../templates/api-controller.md)
