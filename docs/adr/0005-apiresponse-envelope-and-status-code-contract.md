# ADR-0005: A uniform `ApiResponse<T>` envelope with a strict status-code contract

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Boilerplate maintainers

---

## Context

Client error handling is only as good as the API's consistency. If one endpoint returns a bare
object, another returns `{ data: ... }`, a third returns `200` with `{ success: false }`, and a
fourth returns a raw exception string, then every call site needs its own handling and none of
them are quite right. Correlating a user's report with a server log needs a shared identifier
that the user can actually see and read out.

There is a genuine tension here. RFC 9457 (`application/problem+json`) is the standardised way
to describe HTTP errors and is well supported by ASP.NET Core out of the box. An envelope,
meanwhile, is often criticised as re-inventing HTTP semantics inside the response body — and
that criticism is fair when the envelope *replaces* status codes.

The decision below tries to take the useful part of both: keep HTTP status codes fully
meaningful, and add a small, uniform body shape on top of them.

## Decision

Every response — success and failure — is `ApiResponse<T>`:

```json
{
  "success": true,
  "data": { },
  "message": null,
  "errors": null,
  "statusCode": 200,
  "code": null,
  "timestamp": "2026-08-02T00:00:00Z",
  "traceId": "00-…"
}
```

Collections use `PagedResponse<T>`, carrying `items`, `page`, `pageSize`, `totalCount`, and
`totalPages`.

**Rules:**

1. **The HTTP status code is authoritative.** `success` mirrors it; it never contradicts it.
   `200` with `success: false` is forbidden — that is the failure mode the envelope is most
   often accused of, and it is banned outright here.
2. **The status-code contract:** `200` read, `201` created with a `Location` header, `204`
   delete, `400` validation, `401` unauthenticated, `403` unauthorised, `404` missing, `409`
   conflict or optimistic-concurrency failure, `422` domain-rule violation, `429` rate-limited,
   `500` unhandled.
3. **`code` is stable, `message` is not.** `code` is `SCREAMING_SNAKE_CASE`
   (`VALIDATION_ERROR`, `CONFLICT`), part of the contract, and the
   only thing a client may branch on. `message` is for humans and may be reworded or localised
   at any time.
4. **`traceId` is always present** and matches the correlation identifier in the logs and the
   distributed trace. It is surfaced in the UI's error state so a user can quote it.
5. **Controllers never construct an error response.** Handlers throw typed exceptions; an
   `IExceptionHandler` chain — Validation → Conflict → Forbidden → Unhandled — maps them to
   status code and `code`. Unhandled exceptions never leak a stack trace or an internal message.
6. **A result filter applies the envelope**, so controllers return the payload and stay thin.
7. **New `code` values are declared in the spec before use** and listed in the OpenAPI document,
   so they reach the frontend through [ADR-0004](0004-openapi-generated-frontend-types.md).

## Consequences

### Positive

- One unwrapping path in `api-client`, one error-handling path in the UI, one place to add
  cross-cutting response behaviour.
- `traceId` on every response makes support tractable: the identifier the user reads out is the
  identifier in the logs.
- Stable `code` values let the UI react meaningfully — a `CONFLICT` can offer to
  reload, a `VALIDATION_ERROR` can map `errors[]` onto form fields — without parsing prose.
- Adding a field to the envelope is backward compatible for every existing client.

### Negative

- It is not RFC 9457. Tooling and middleware that expect `problem+json` need adapting, and
  reviewers who know the RFC will (reasonably) ask why. Rule 1 is the answer: we keep HTTP
  semantics intact and add to them, rather than replacing them.
- Every payload carries envelope overhead. Negligible per response, non-zero over a large list.
- `statusCode` in the body duplicates the HTTP status. Deliberate redundancy — useful when a
  response is logged or persisted apart from its transport — but it is redundancy, and it must
  never disagree with the header.
- Generated client types are nested one level deeper (`response.data.items`), which is slightly
  more verbose at every call site.

### Neutral

- The exception-handler chain becomes the single place error mapping is defined and reviewed.
- `PagedResponse<T>` fixes the paging vocabulary early, which is a constraint as much as a
  convenience.

### Follow-on work

- Consider emitting `problem+json` alongside the envelope for `5xx` responses if a downstream
  consumer requires it.

## Alternatives considered

### RFC 9457 `application/problem+json` for errors, bare payloads for success

The standards-compliant choice, and a defensible one. Rejected because success and failure then
have different shapes, so the client needs two code paths and cannot carry a `traceId`, a
correlation field, or paging metadata uniformly on success responses.

### Bare payloads with everything in headers

Cleanest bodies. Rejected: headers are awkward to reach in browser code, invisible in most
logs and API clients, and easily dropped by intermediaries.

### GraphQL-style `200` for everything with errors in the body

Rejected explicitly. It breaks caching, monitoring, load-balancer health signals, and every tool
that reasons about HTTP status codes.

### No convention — per-endpoint judgement

Rejected. This is the status quo the decision exists to prevent.

## Verification

Integration tests assert the envelope and the status-code mapping for each handler in the chain.
A controller that builds an error response by hand is a review rejection. Any endpoint returning
`200` with `success: false` is a defect.

## References

- `.claude/standards/` — API response format and error handling standards
- [docs/specs/TEMPLATE.md](../specs/TEMPLATE.md) — §3 requires new `code` values up front
