# Standard: Error Handling

Errors are handled centrally, returned in the standard envelope, and never leak internals.

## Principles

- **Central handling.** A chain of `IExceptionHandler` implementations
  (Validation → Conflict → Forbidden → NotFound → Unhandled) is the single place that turns
  an exception into a client response. Controllers never build error envelopes by hand
  (see [`middleware.md`](middleware.md)).
- **Standard envelope.** Every error returns `ApiResponse` / `ApiResponse<T>` with
  `success=false`, a human `message`, a machine-readable `code`, optional `errors[]`,
  `statusCode`, and `traceId` (see [`api-response-format.md`](api-response-format.md)).
- **No leakage.** Never return stack traces, SQL errors, EF Core exception text, or internal
  exception messages to the client. In Production the client gets a generic message + `code`
  + `traceId`; the full detail is logged against that same `traceId`.

## Exception → response mapping

| Condition | Status | `code` |
|---|---|---|
| Malformed body / model binding failure | 400 | `BAD_REQUEST` |
| FluentValidation failure | 400 | `VALIDATION_ERROR` (messages in `errors[]`) |
| Missing / invalid / expired credentials | 401 | `UNAUTHENTICATED` |
| Authenticated but not permitted (existence is not secret) | 403 | `FORBIDDEN` |
| Resource missing, **or** hidden from this caller | 404 | `NOT_FOUND` |
| Duplicate key / invalid state transition | 409 | `CONFLICT` |
| Optimistic-concurrency mismatch (`DbUpdateConcurrencyException`) | 409 | `CONFLICT` |
| Resource permanently withdrawn (expired one-time link, retired resource) | 410 | `RESOURCE_GONE` |
| Rate limit exceeded | 429 | `RATE_LIMITED` |
| Unexpected fault | 500 | `INTERNAL_ERROR` (generic message only) |
| Dependency unavailable / maintenance mode | 503 | `SERVICE_UNAVAILABLE` |

The full status-code table, including the **hide-as-404**, **409-for-optimistic-concurrency**,
and **410-vs-404** conventions, is in
[`api-response-format.md`](api-response-format.md#http-status-codes--the-full-table). Both
documents must agree; if you change one, change the other.

- **Domain exceptions** (`DomainException` and its subtypes) map to 409 with a specific `code`
  and the domain message — these are safe, business-meaningful messages, not internal detail.
- **Validation exceptions** map to 400 with per-field messages in `errors[]`.
- Distinguish "expected" business failures (safe to surface) from "unexpected" faults
  (generic message + logged detail).

## Handler chain shape

```csharp
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<ConflictExceptionHandler>();
builder.Services.AddExceptionHandler<ForbiddenExceptionHandler>();
builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();  // always last
builder.Services.AddProblemDetails();
```

Order matters: each handler returns `true` only for the exception types it owns; the
unhandled handler is the terminal catch-all and is the only one that emits a generic message.

## Logging & telemetry

- Log every handled 5xx and every unexpected exception at `Error` with the `traceId`, actor
  id/role, route, and correlation id. Never log secrets, tokens, or full PII.
- Business-rule 4xx are logged at `Information` / `Warning` — they are not system faults.
- Correlate logs, traces, and the client-visible `traceId` (see
  [`observability-tracing.md`](observability-tracing.md)).

## Rules

- No empty catch blocks; no silently swallowed exceptions.
- No `catch (Exception) { return generic 200 }` — surface the correct status.
- Cancellation (`OperationCanceledException` from a client abort) is not an error; do not log
  it as one and do not return 500 for it.
- Security-relevant failures (authentication, authorization, expired/revoked token use) are
  logged as security events without leaking sensitive data (see
  [`owasp-security.md`](owasp-security.md)).
- Retryable transient faults (`503`) set `Retry-After` where the value is known.

## Related

[`api-response-format.md`](api-response-format.md) · [`middleware.md`](middleware.md) · [`observability-tracing.md`](observability-tracing.md) · [`security.md`](security.md)
