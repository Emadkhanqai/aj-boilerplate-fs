# Standard: .NET / ASP.NET Core Security

Microsoft-aligned hardening for the backend. Complements
[`owasp-security.md`](owasp-security.md) and [`security.md`](security.md).

## Identity: the provider authenticates, Keycloak authorizes

- **Authentication** is delegated to the cloud provider's identity service over **OIDC**
  (Google Cloud Identity under `CLOUD_PROVIDER=gcp`, Microsoft Entra ID under `azure`).
  Validate the JWT with `JwtBearer` against the configured authority and audience. The
  application holds **no local credentials**.
- **Authorization** is **Keycloak**, in both providers. It is the source of truth for roles,
  scopes, and permissions, and it mints the scoped, time-bound, revocable tokens behind any
  external surface. This split is deliberate: swapping cloud provider must not change the
  authorization model.
- Map Keycloak roles/permissions into ASP.NET Core **authorization policies**. Cache permission
  lookups (distributed cache) with a short TTL and an explicit invalidation path. Enforce
  **deny by default**.

```csharp
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();            // deny by default; opt out per endpoint, deliberately
});
```

## Authorization enforcement

- A policy on **every** endpoint (function-level authorization). No anonymous business
  endpoint.
- **Object-ownership / scope check inside the handler after loading the resource** — never
  trust the client's id (IDOR/BOLA).
- **Per-permission DTO projection** for restricted fields; a disallowed response type cannot
  carry them. Covered by an integration test.

## Input & data safety

- **DTOs only at the boundary; never bind EF Core entities** (prevents mass assignment /
  over-posting).
- **FluentValidation on every command/request** (see
  [`input-validation-sanitization.md`](input-validation-sanitization.md)).
- **Parameterised EF Core only.** No `FromSqlRaw` with string interpolation; if raw SQL is
  genuinely needed use `FromSqlInterpolated` or explicit parameters, and justify it in review.
- Monetary values are `decimal` with explicit precision — never `float`/`double` (see
  [`efcore-migrations.md`](efcore-migrations.md)).
- Deserialization: `System.Text.Json` with default (safe) settings. Never enable polymorphic
  type-name handling on untrusted input.

## Transport & configuration

- **HTTPS only; HSTS** in non-Development.
- **Security headers** via middleware: `X-Content-Type-Options: nosniff`,
  `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a strict `Content-Security-Policy`,
  `Permissions-Policy`; strip `Server`.
- **CORS:** an explicit per-environment origin allow-list; never `*` with credentials.
- **Rate limiting** and **request body-size limits** (see [`middleware.md`](middleware.md)).
- **Forwarded headers** restricted to known proxies — an unrestricted
  `ForwardedHeadersOptions` lets a caller spoof its own IP and defeat IP-based rate limiting.
- **Antiforgery** wherever cookie auth applies. Bearer APIs are CSRF-resistant, but any cookie
  introduced must be `Secure`, `HttpOnly`, and `SameSite`-scoped.

## Secrets

- **No secrets in source or committed configuration.** The provider's secret store behind
  `ISecretsProvider` in the cloud; `dotnet user-secrets` locally. Never log secrets,
  connection strings, or tokens.
- Rotate on any suspicion of exposure. Treat a secret that ever reached git history as
  compromised.

## Machine-to-machine access

If another service calls this API, authenticate it **separately from user SSO** — workload
identity where the platform supports it, client credentials otherwise. Scope its token to
exactly the resources it needs and nothing more, and keep the integration behind a port
(`IIntegrationGateway`) so the mechanism can change without touching the domain.

## External scoped links

Any link-based external surface uses a signed, server-validated, single-purpose token scoped
to exactly one resource, expiring and revocable. Store a **hash** of the token. An expired or
revoked link grants nothing, and the attempt is audited.

## Related

[`owasp-security.md`](owasp-security.md) · [`security.md`](security.md) · [`middleware.md`](middleware.md) · [`cloud.md`](cloud.md)
