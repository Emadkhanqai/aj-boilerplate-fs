# ADR-0004: The frontend's API types are generated from OpenAPI, never hand-written

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Boilerplate maintainers

---

## Context

When a TypeScript interface is written by hand to mirror a C# DTO, the two are correct on the
day they are written and drift from that day forward. The drift is silent: TypeScript happily
compiles against a shape the server stopped sending months ago, and the failure surfaces at
runtime, in a browser, usually in production, usually as `undefined`.

The failure modes are boringly consistent — a field renamed on the server, a nullable field the
client treats as required, an enum gaining a member the client's union does not have, a number
that became a string. None of these are caught by any test that does not talk to the real API.

The API already produces an OpenAPI document. That document is derived from the actual
controllers and actual contract types, so it cannot drift from the server. The question is only
whether the client trusts it.

## Decision

We will generate the frontend's API types from the API's OpenAPI document, and hand-writing a
type that mirrors a server contract is a defect.

- `libs/data-access/api-types` is **generated output**. It is committed (so builds are
  reproducible and diffs are reviewable) and never hand-edited.
- Regeneration is `npm run generate:api`, which reads the running API's OpenAPI document and
  rewrites the library. The `/sync` command does this and then checks for duplicated DTOs and
  unversioned endpoint usage.
- `libs/data-access/api-client` is hand-written and thin: it wires HTTP calls, unwraps the
  `ApiResponse<T>` envelope from [ADR-0005](0005-apiresponse-envelope-and-status-code-contract.md),
  and exposes typed methods. It imports its types from `api-types` and defines none of its own.
- **OpenAPI first.** A contract change is agreed in the spec, lands in the API, and is then
  regenerated. The sequence is never "write the client type, then make the server match".
- View models are welcome — a type shaped for a specific screen is fine. What is banned is a
  hand-written *duplicate* of a server contract type.
- If the generated output is wrong, the fix is in the API's OpenAPI annotations. Never patch the
  generated file.

## Consequences

### Positive

- A breaking server change breaks the frontend build, which is the earliest and cheapest place
  to find it. This single property is the entire justification.
- Nullability, enums, and formats come across accurately, because they come from the same
  metadata the server serialises with.
- Reviewing a contract change is reviewing a diff of the generated file — the blast radius is
  visible.
- Agents cannot invent a plausible-looking DTO, because inventing one is a rule violation with
  an obvious tell.

### Negative

- Generation requires a running API (or a committed OpenAPI document), which is friction for a
  frontend-only developer and for the frontend-only repository.
- Generated types follow the generator's conventions, not the team's. They are sometimes ugly.
- A large contract change produces a large mechanical diff that reviewers are tempted to skim.
- Forgetting to regenerate is a real and common mistake; it degrades to the hand-written
  situation until someone notices.

### Neutral

- The generated file is committed, so it appears in pull requests and in line counts.
- The API's OpenAPI annotations become load-bearing: sloppy `[ProducesResponseType]` attributes
  now produce sloppy client types, which is arguably a feature.

### Follow-on work

- A CI check that regenerates and fails on a non-empty diff would close the "forgot to
  regenerate" gap. Worth adding once the API has a stable published document per environment.

## Alternatives considered

### Hand-written TypeScript interfaces

Full control, no tooling, no running API required. Rejected — this is precisely the drift
problem described above, and no amount of discipline has ever solved it on a team of more than
one.

### A shared schema language (Protobuf, JSON Schema) as the source of truth

Genuinely good, and language-neutral. Rejected as too heavy for a boilerplate: it adds a build
step and a schema repository, and it duplicates information the C# contract types already carry.
OpenAPI generated *from* the code keeps one source of truth rather than creating a second.

### Generating at build time instead of committing the output

No stale file, no forgetting. Rejected because it makes every frontend build depend on a running
backend, and it hides contract changes from code review — the diff is where the value is.

### Runtime validation only (parse responses against a schema)

Catches drift, but at runtime and in the user's browser. Complementary at best, not a
replacement for compile-time typing.

## Verification

`libs/data-access/api-types` has no hand-authored commits. Any `interface` or `type` in feature
code that mirrors a server contract is a review rejection. `/sync` reports duplicated DTOs.

## References

- [docs/api/README.md](../api/README.md)
- [ADR-0005](0005-apiresponse-envelope-and-status-code-contract.md) — the envelope the client unwraps
