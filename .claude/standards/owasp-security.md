# Standard: OWASP Security

Align to **OWASP Top 10 (2025)**, the **OWASP API Security Top 10 (2023)**, and Microsoft's
ASP.NET Core security guidance. **Broken Access Control is the highest risk** for an
authenticated business API — treat it as such in every review.

## Highest risk — Broken Access Control / BOLA / IDOR

- **Enforce role *and* scope authorization on every operation.** Deny by default.
- **Never rely on frontend authorization.** Role-aware UI is UX; the backend re-checks every
  time.
- **Validate object ownership on every operation.** Load the resource, then assert the caller
  is entitled to it — do not trust an id from the client. This is OWASP API #1 (BOLA/IDOR).
- **Broken Object Property Level Authorization (API #3):** when a caller may read a resource
  but not one of its fields, **project a different DTO per permission level** so the
  disallowed payload cannot structurally contain the value. Never by client-side hiding, never
  by returning it and styling it away. A test asserts there is no leak.

## Mapped controls — OWASP Top 10 (2025)

- **Broken Access Control** → per-operation role/scope/ownership checks; deny by default.
- **Security Misconfiguration** → security headers, strict CORS, HSTS, no public Swagger UI in
  production, no verbose errors (see [`middleware.md`](middleware.md),
  [`dotnet-security.md`](dotnet-security.md)).
- **Software Supply Chain Failures** → pin dependencies, review transitive packages, no
  unvetted packages; dependency scanning in the gate.
- **Cryptographic Failures** → TLS only; secrets in the provider's secret store; no home-grown
  crypto; hash tokens rather than storing them.
- **Injection** → parameterised EF Core only; no string-built SQL; validate and encode all
  input.
- **Insecure Design** → threat-model external surfaces, integrations, and exports *during*
  design, not after.
- **Authentication Failures** → SSO only, no local credentials; external tokens are
  single-purpose, time-bound, and revocable.
- **Software/Data Integrity Failures** → signed tokens; append-only audit; reviewed
  migrations; no unpinned CI actions.
- **Security Logging & Alerting Failures** → log security events with `traceId` and without
  leaking secrets or PII.
- **Mishandling Exceptional Conditions** → central error handling, no leakage, correct status
  codes (see [`error-handling.md`](error-handling.md)).

## Mapped controls — OWASP API Security Top 10 (2023)

| # | Risk | Control in this codebase |
|---|---|---|
| 1 | BOLA | Ownership check after load, in the handler |
| 2 | Broken authentication | OIDC SSO; no local credentials; short-lived tokens |
| 3 | Broken object property level auth | Per-permission DTO projection + a leak test |
| 4 | Unrestricted resource consumption | Rate limiting, body-size limits, mandatory pagination, request timeouts |
| 5 | Broken function level auth | An explicit policy on every endpoint; deny by default |
| 6 | Unrestricted access to sensitive business flows | Guard state transitions and one-time actions with idempotency + server-side state checks |
| 7 | SSRF | Outbound URLs validated against an allow-list; never fetch a client-supplied URL |
| 8 | Security misconfiguration | Headers, CORS, HSTS, environment-specific config parity |
| 9 | Improper inventory management | Versioned, documented APIs (see [`api-versioning.md`](api-versioning.md)) |
| 10 | Unsafe consumption of APIs | Validate every upstream response; never trust a partner payload |

## Operational rules

- **Use DTOs. Never bind EF Core entities** from API requests — prevents mass assignment.
- **Validate every command/request** with FluentValidation (see
  [`input-validation-sanitization.md`](input-validation-sanitization.md)).
- **Parameterised EF Core queries only.** Raw SQL must be justified, reviewed, and
  parameterised.
- **No secrets in the repo.** Provider secret store only.
- **HTTPS enforced; CORS strict; security headers on.**
- **Limit export/report endpoints** — authorize, rate-limit, cap size, stream, and never
  include data the caller may not otherwise see.
- **Sanitize free text where displayed** (defence in depth against stored XSS).
- **Audit all business-critical actions** (append-only).
- **Review OWASP risks during code review** ([`../commands/review.md`](../commands/review.md))
  and **fix every SonarQube Blocker/Critical/Major before push**
  ([`sonarqube.md`](sonarqube.md)).

## Related

[`security.md`](security.md) · [`dotnet-security.md`](dotnet-security.md) · [`input-validation-sanitization.md`](input-validation-sanitization.md) · [`error-handling.md`](error-handling.md)
