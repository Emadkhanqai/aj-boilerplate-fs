# The API contract

**The OpenAPI document is the contract.** It is the single source of truth for every request
shape, response shape, status code, and error code that crosses the boundary between the two
stacks. If something is not in the OpenAPI document, the frontend cannot rely on it.

See [ADR-0004](../adr/0004-openapi-generated-frontend-types.md) for why, and
[ADR-0005](../adr/0005-apiresponse-envelope-and-status-code-contract.md) for the envelope every
response uses.

---

## Where the document comes from

It is produced **from the code**, not written by hand. The API generates it from the controllers
and the types in `AjBoilerplate.Contracts`, so it cannot describe an endpoint that does not
exist or a shape the server does not actually serialise.

[`openapi.json`](openapi.json) in this folder is the **committed snapshot**, and it is the file the
frontend generates from. Regenerate it whenever you change the contract:

```bash
cd src/backend
./scripts/generate-openapi.sh            # rewrite openapi.json
./scripts/generate-openapi.sh --check    # fail if it is out of date (what CI runs)
```

Neither needs a running API or a database — the script loads the built assembly and asks the same
`ISwaggerProvider` the `/swagger` endpoint uses.

**CI fails the build when the snapshot and the API disagree** (the `OpenAPI contract snapshot` job in
`.github/workflows/backend-ci.yml`). That is what makes this document a contract rather than a
description of one: without the gate, an endpoint could be renamed or a field dropped and nothing
would object until a client broke. When it fails, read the diff — if the change was intended,
regenerate and commit the snapshot alongside the code change so the contract change is *reviewed*
rather than discovered; if it was not, fix the API. Regenerating to silence the gate defeats it
entirely.

With the API running you can also browse it live:

| Artefact | URL |
|---|---|
| OpenAPI JSON | <http://localhost:5080/swagger/v1/swagger.json> |
| Interactive UI | <http://localhost:5080/swagger> |

The quality of the document is entirely determined by the quality of the annotations. That makes
the following non-optional on every action:

- `[ProducesResponseType(typeof(ApiResponse<XDto>), StatusCodes.Status200OK)]` — and one for
  **every** status code the action can return, including the failures.
- `[ApiVersion]` and a versioned route: `/api/v{version}/{resource}`.
- XML documentation comments on contract types and their properties — they become the
  descriptions in the generated client and in the UI.
- Accurate nullability. A `string?` and a `string` produce different TypeScript, and the
  difference is the whole point.
- Enums declared as enums, not as strings, so the client gets a union type.

An action with a single `ProducesResponseType` for the happy path produces a client that has no
idea how the endpoint fails. Treat a missing failure annotation as a bug.

---

## How the frontend consumes it

```bash
# from the committed snapshot — no API process required
cd src/frontend
npm run generate:api
```

This rewrites `libs/data-access/api-types` from the live document. That library is **generated
output**:

- It is committed, so builds are reproducible and contract changes show up in code review.
- It is never hand-edited. If the output is wrong, fix the annotations in the API and regenerate.
- Nothing else in the workspace declares a type that mirrors a server contract.

`libs/data-access/api-client` is the hand-written layer on top. It is thin by design: it makes
the HTTP call, unwraps the `ApiResponse<T>` envelope, maps a failure `code` to something the UI
can act on, and returns typed data. It imports every type from `api-types` and declares none of
its own.

Screen-specific view models are fine and expected. A hand-written *duplicate* of a server
contract type is not.

The `/sync` command runs the regeneration and then checks for duplicated DTOs and for endpoints
being called without a version prefix.

---

## Changing the contract

The order matters, and it is always this order:

1. **Spec.** Agree the endpoints, DTOs, status codes, and error codes in
   [the spec template](../specs/TEMPLATE.md) §3, before any code. New `code` values are declared
   there.
2. **Server.** Implement it, with complete annotations.
3. **Regenerate.** `npm run generate:api` (or `/sync`).
4. **Consume.** Update the client and the feature code against the new types.
5. **Review.** The generated diff is part of the pull request and is reviewed, not skimmed.

Never run this backwards. Writing the client type first and making the server match it is how a
contract stops describing the system.

---

## Versioning and breaking changes

Routes are versioned from day one: `/api/v1/items`. Version 1 exists before there is any reason
for a version 2, so introducing one is a routine change rather than a migration.

A change is **breaking** if it removes or renames a field, narrows a type, makes an optional
field required, changes a status code, changes an existing `code` value, or removes an enum
member. Breaking changes need a new API version and an ADR.

A change is **additive** — and safe within a version — if it adds an optional field, adds a new
endpoint, adds a new `code` value, or adds an enum member the client can treat as unknown.

The spec template's breaking-change checklist (§3) exists to force this judgement before the code
is written rather than after the client breaks.

---

## Error handling on the client

Branch on `code`. Never on `message`.

```ts
// code is stable and part of the contract; message is human-readable and may change.
if (error.code === 'CONFLICT') { /* offer to reload */ }
```

Every error state in the UI surfaces the response's `traceId`, because that is the value a user
can read out to support and support can find in the logs.

---

## For the frontend-only repository

`aj-boilerplate-fe` has no backend to generate from, so it ships a committed OpenAPI document
that `npm run generate:api` reads from disk instead of over HTTP. Keep that document current
with whichever API the project actually talks to — a stale committed contract is worse than no
contract, because it looks authoritative.
