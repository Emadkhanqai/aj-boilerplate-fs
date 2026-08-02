# ADR-0001: Layered Clean Architecture, not vertical slices

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Boilerplate maintainers

---

## Context

The backend needs a structure that a new team can follow without a week of orientation, that
keeps business rules out of controllers and out of the database layer, and — critically for a
boilerplate — that can be *enforced* rather than merely recommended.

Two structures were on the table.

**Layered (onion) Clean Architecture:** one project per concentric layer — `Domain`,
`Application`, `Contracts`, `Infrastructure`, `Api` — with feature folders inside each layer.
Dependency direction is a property of the project references, so the compiler and a small
architecture test suite can enforce it absolutely.

**Vertical slices:** one folder per feature containing its own request, handler, validator,
persistence, and endpoint. Locality of change is excellent; a feature is one directory.

Vertical slices are frequently the better choice for a large product with many independent
teams, and the internal development guidance we started from leans that way. But two facts
pushed the other direction. First, this codebase is derived from a working production system
that is layered — the structure is proven here, not hypothetical. Second, and more decisive: in
a boilerplate the *enforceability* of a rule matters more than its ergonomics. A vertical-slice
boundary is a naming convention. A project-reference boundary is a compile error.

## Decision

We will use layered Clean Architecture with five source projects, feature folders *inside* each
layer, and this dependency direction:

```
Api → Infrastructure → Application → Domain
Api → Contracts ← Application
```

- `Domain` references nothing — no EF Core, no ASP.NET, no third-party framework.
- `Application` references `Domain` and `Contracts`, declares ports as interfaces, and never
  references `Infrastructure` or `Api`.
- `Infrastructure` implements those ports and is the only project that knows about EF Core,
  Redis, HTTP clients, or a cloud SDK.
- `Api` composes: thin controllers, middleware, dependency injection.

`AjBoilerplate.ArchitectureTests` asserts every one of these rules and runs as its own CI job.
A violation fails the build; it is not a review comment someone can wave through.

Feature folders inside each layer (`Domain/Items/`, `Application/Items/`, …) recover much of the
locality that vertical slices offer: a feature is still a predictable set of paths, just fanned
across five projects instead of one.

## Consequences

### Positive

- The dependency rule is mechanically enforced, so it cannot erode. This is the whole point.
- Business logic has an unambiguous home. "Where does this go?" has one answer.
- `Domain` and `Application` are testable with no infrastructure at all, which keeps the unit
  suite fast enough to run on every file save via the `run-affected-tests` hook.
- Swapping an infrastructure concern — the secrets provider, per [ADR-0002](0002-dual-cloud-provider-behind-one-switch.md)
  — touches one project.

### Negative

- A trivial feature touches five projects. This is real friction and it is felt most on the
  smallest changes.
- The layer boundary invites mapping code: domain entity → DTO, DTO → response. Some of it is
  genuine anti-corruption; some is ceremony.
- Teams unfamiliar with the style tend to create anaemic domain entities and push logic into
  `Application` handlers, quietly rebuilding a transaction script. The architecture tests cannot
  detect that.

### Neutral

- Five source projects plus three test projects is more solution scaffolding than a single-project
  API. Build times are marginally higher and the solution file is longer.

### Follow-on work

- Keep the architecture tests current as layers gain responsibilities.

## Alternatives considered

### Vertical slices with MediatR

Better change locality and less mapping. Rejected because the boundary is a convention rather
than a compile-time constraint — exactly the thing that degrades once a team is under deadline,
and exactly what a boilerplate should protect against. It also gives newcomers no single answer
to "where does business logic live?", which in practice means it lives in whichever file is
already open.

### A single project with folders

Fastest to start, and honest for a service that will stay small. Rejected because a boilerplate
is by definition the seed of something that grows, and this structure has no mechanism at all to
resist that growth.

### Layered, but merging `Contracts` into `Application`

One fewer project. Rejected because the frontend's generated types are produced from the shapes
in `Contracts`; keeping them in a project with no dependencies except serialisation makes it
obvious that changing one is a contract change, per [ADR-0005](0005-apiresponse-envelope-and-status-code-contract.md).

### Doing nothing (leaving structure to each consuming team)

Rejected. Structure is most of what a boilerplate is for.

## Verification

`AjBoilerplate.ArchitectureTests` is green. If a team finds itself adding suppressions to those
tests, or routinely reaching from `Application` into `Infrastructure`, that is the signal to
write a superseding ADR rather than to weaken the suite.

## References

- `.claude/standards/clean-architecture.md`
- [ADR-0005](0005-apiresponse-envelope-and-status-code-contract.md) — the contract the `Api` layer exposes
