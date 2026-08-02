# Standard: Middleware Pipeline

A clean, ordered, production-grade middleware pipeline. **Order matters** — it is documented
here and reviewed on every backend architecture review, because incorrect order causes
security and authorization bugs. Microsoft explicitly notes that middleware order matters, and
**endpoint-specific rate limiting must run after routing** so the endpoint is known.

---

## Canonical order (authoritative)

This is the exact `Program.cs` order. Do not reshuffle without a reviewed reason.

1. **Global exception handling** — outermost (`UseExceptionHandler`, backed by the
   `IExceptionHandler` chain).
2. **Forwarded headers** — honour `X-Forwarded-*` from the platform load balancer so scheme,
   host, and client IP are correct *before* HTTPS/HSTS/rate-limit decisions. Restrict to known
   proxies.
3. **HTTPS redirection.**
4. **HSTS** — non-Development only.
5. **Security headers.**
6. **Correlation ID / trace ID.**
7. **Request/response logging** (sanitised).
8. **Routing** (`UseRouting`) — must precede CORS, rate limiting, and auth so endpoint
   metadata is available.
9. **CORS.**
10. **Rate limiting** — global + endpoint-specific policies (after routing).
11. **Request timeouts.**
12. **Authentication.**
13. **Authorization.**
14. **Anti-CSRF** — only if cookie-based auth is used.
15. **Validation filters** — action/endpoint filters returning the standard envelope.
16. **Output caching** — safe GET endpoints only.
17. **Response compression.**
18. **Endpoints / controllers** (`MapControllers`).
19. **Health checks** (`/health/live`, `/health/ready`).

Cross-cutting concerns that are **not** ordered middleware but are enforced at the Application
edge: input validation/sanitisation, audit logging, idempotency, and optimistic concurrency
(ETag / `rowversion`). They live at the boundary where business context exists.

---

## 1. Correlation ID middleware

- Accept `X-Correlation-ID` from trusted callers; **generate one if missing**.
- Return it in the response headers.
- Include it in every log scope and in `ApiResponse.traceId` (source:
  `Activity.Current?.Id ?? HttpContext.TraceIdentifier`). See
  [`observability-tracing.md`](observability-tracing.md).

## 2. Request/response logging middleware

- Log method, path, status code, duration, actor id, actor role, correlation id.
- **Never log** passwords, tokens, authorization headers, connection strings, or full request
  bodies by default. Maintain an explicit redaction list and unit-test it.

## 3. Security headers middleware

Set `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: no-referrer`, `Permissions-Policy`, a strict `Content-Security-Policy` where
applicable, and `Strict-Transport-Security` (non-Development). Strip the `Server` header.

## 4. Rate limiting middleware

- Stricter limits on authentication callbacks, anonymous or token-scoped external endpoints,
  and export/report endpoints.
- Use endpoint-specific policies (`RequireRateLimiting("policy")`); runs **after routing**.
- Return `429` in the standard `ApiResponse` envelope, with `Retry-After`.

## 5. Request timeout middleware

- A default timeout policy, plus longer named policies for legitimately slow endpoints
  (report generation, bulk export). Never raise the global default to accommodate one route.

## 6. Request body size limits

- A tight global default. Endpoints that legitimately accept larger payloads (import, upload)
  declare their own explicit, per-endpoint limit.

## 7. CORS middleware

- Strict allow-list only, per environment. **No wildcard origins in staging or production, and
  never a wildcard combined with credentials.**

## 8. Global exception middleware

- Convert **all** exceptions to the standard `ApiResponse`.
- **Never expose** stack traces or SQL/EF Core exception detail.
- Always include `traceId`. See [`error-handling.md`](error-handling.md).

## 9. Validation filter

- Return validation errors in `ApiResponse.errors` with `code = VALIDATION_FAILED`. See
  [`input-validation-sanitization.md`](input-validation-sanitization.md).

## 10. Output encoding / sanitisation boundary

- Encode free-text fields when displayed or exported. **Store raw, encode on output**; the
  frontend escapes as well (see [`angular.md`](angular.md)).
- Reject control characters and oversized text at validation time.

## 11. Anti-CSRF

- **Required if cookie-based auth is used.** Not required for pure bearer-token APIs — but the
  decision is recorded here. The boilerplate ships **bearer tokens**, so CSRF middleware is
  **off**. Introducing any auth cookie flips this on; that is an ADR-worthy change.

## 12. Authentication + authorization middleware

- Enforce **deny by default**: every endpoint declares a policy; no anonymous business
  endpoint.
- **Protect against IDOR/BOLA** — validate object ownership *after* loading the resource, in
  the handler, never from a client-supplied id alone.
- **Never trust frontend-only permission checks.** See
  [`owasp-security.md`](owasp-security.md), [`dotnet-security.md`](dotnet-security.md).

## 13. Audit logging boundary

- Audit business-significant actions (create, update, state transition, permission change,
  configuration change) with actor, timestamp, action, and prior → new values.
- **Append-only.** Implemented at the Application/domain boundary — where business context
  exists — not as raw HTTP middleware.

## 14. Health checks

- `/health/live` (process is up) and `/health/ready` (dependencies reachable: database, cache,
  identity provider). Used by the platform's health/readiness probe (see
  [`cloud.md`](cloud.md)).

## 15. Response compression

- Use for JSON responses where beneficial.
- **Do not compress responses that mix secrets or CSRF tokens with attacker-influenced input**
  (BREACH-style risk).

## 16. Output caching

- **Safe GET endpoints only.**
- **Never cache user-specific or permission-scoped payloads** unless the cache key is varied
  by the identity/scope that determines the content.
- Never cache responses served against a one-time or scoped token.

## 17. Localization middleware

- Culture must be resolved **before** validation messages are produced, if messages are
  localized. Ship the hook even if only one language is live, and keep layouts RTL-tolerant.

## 18. API deprecation headers

- For superseded API versions, return `Deprecation` / `Sunset` headers alongside
  `api-deprecated-versions`. See [`api-versioning.md`](api-versioning.md).

## 19. Maintenance mode

- Optional, operator-controlled downtime during migrations/releases. Returns `503` with a
  maintenance `ApiResponse` and `Retry-After` while enabled.

## 20. Idempotency filter

- Recommended for critical, non-repeatable POSTs. Honour an `Idempotency-Key` header: store
  the first result and replay it for retries and double-clicks, so a duplicate submission
  cannot create a duplicate record.

## 21. ETag / concurrency headers

- Emit `ETag` on entities that support optimistic concurrency; require `If-Match` on updates.
- **Pair with a database `rowversion`** (see [`efcore-migrations.md`](efcore-migrations.md));
  return `409` with `code = CONCURRENCY_CONFLICT` on mismatch (see
  [`api-response-format.md`](api-response-format.md)).

## 22. Scoped-token endpoints (external / anonymous surfaces)

If the project exposes a link-based external surface, validate **token hash, expiry,
revocation, scope, and one-time-use status** on every request to it.
**Never store the raw token — store a hash and compare hashes.** Log expired, revoked, and
invalid attempts as security events. An expired or revoked token returns `410` or `404` per
the [`api-response-format.md`](api-response-format.md) conventions — never a hint that the
token was once valid unless that is deliberate.

## 23. Machine-to-machine endpoints

If a downstream system calls this API service-to-service, protect it **separately from user
authentication** (workload identity or client credentials), and scope its access to exactly
the resources it needs — never a broad read of the whole domain.

## 24. Export / report endpoint protection

- **Authorize every export.** An export must never contain data the caller could not read
  through the ordinary API.
- Rate-limit, cap size, and stream large outputs.

## 25. Ordering documentation & review rule

- The canonical order above is the authoritative record.
- **Middleware order is reviewed during every backend architecture review.** Incorrect order
  causes security and authorization bugs. Endpoint-specific rate limiting runs after routing.

---

## Rules

- Middleware contains **no business logic** — cross-cutting only. Business rules live in
  Domain/Application; audit, idempotency, and concurrency are enforced at that boundary.
- Keep `AddProblemDetails()` for framework-level faults; ensure `traceId` appears in both the
  `ApiResponse` and `ProblemDetails` outputs.
- Any reorder of the pipeline is a reviewed change with a documented reason (an ADR).

## Related

[`error-handling.md`](error-handling.md) · [`dotnet-security.md`](dotnet-security.md) · [`owasp-security.md`](owasp-security.md) · [`observability-tracing.md`](observability-tracing.md) · [`api-response-format.md`](api-response-format.md) · [`efcore-migrations.md`](efcore-migrations.md)
