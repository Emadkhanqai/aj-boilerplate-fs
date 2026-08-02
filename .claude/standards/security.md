# Standard: Security (baseline)

The enterprise security baseline every project built on this boilerplate inherits. Project
specific constraints are added in `docs/specs/` and recorded as ADRs — they never *relax* what
is here.

## Authentication & identity

- **Internal users:** corporate SSO via the cloud provider's identity service (OIDC). The
  application stores **no local credentials** and implements no password flow. Deprovisioning
  at the identity provider removes access on the next session refresh.
- **Authorization:** **Keycloak** is the source of truth for roles, scopes, and permissions,
  and is provider-independent — it works identically under either `CLOUD_PROVIDER` (see
  [`cloud.md`](cloud.md)). Keycloak roles are mapped to ASP.NET Core authorization policies.
- **External / anonymous surfaces:** if the project exposes one, access is a **single-purpose,
  time-bound, revocable, scoped token**, not a guessable URL. Store a hash of the token, never
  the token. An expired or revoked token grants nothing and the attempt is logged.
- **Roles are enforced server-side, every time.** Client-side role awareness is UX only.

## Authorization

- **Deny by default.** Every endpoint declares a policy; no anonymous business endpoint.
- **Object-level checks after loading the resource** — never trust a client-supplied id
  (IDOR/BOLA).
- **Field-level protection by DTO projection.** When a field must not reach a given caller,
  return a *different DTO* that cannot structurally carry it. Hiding it in the UI, or
  returning it and trusting the client, is a vulnerability. A test must assert the field never
  appears in a disallowed caller's response.

## Secrets & data

- **No secrets in source, ever.** `dotnet user-secrets` locally; the provider's secret store
  behind `ISecretsProvider` in the cloud (see [`cloud.md`](cloud.md)).
- The `secret-scan` hook and the CI Gitleaks job are both blocking. A committed secret is
  treated as compromised: rotate first, then remove.
- All text stored as Unicode; all timestamps in UTC.
- Encrypt in transit everywhere; encrypt at rest using the platform's managed keys as a
  minimum.

## API & transport

- **HTTPS only. HSTS** in non-Development.
- Errors never leak stack traces or internal detail (see
  [`error-handling.md`](error-handling.md)).
- Input validated server-side (see
  [`input-validation-sanitization.md`](input-validation-sanitization.md)).
- Parameterised queries only — EF Core does this by default; raw SQL must be parameterised and
  justified in review.
- Rate limiting, request body-size limits, and request timeouts on by default (see
  [`middleware.md`](middleware.md)).

## Audit

- **Append-only audit log** of every business-significant action: actor, timestamp, action
  type, and prior → new values. Entries are never updated or deleted.
- Security events (authentication failure, authorization denial, token revocation, use of an
  expired token, configuration change) are logged as a distinct category with the `traceId`,
  and without the sensitive value that triggered them.

## Supply chain

- Pin dependencies; review transitive additions.
- `dotnet list package --vulnerable --include-transitive` and `npm audit` run in CI and block
  on high severity.
- No unvetted package enters the build because an agent suggested it.

## Quality gate

Security hotspots and vulnerabilities reported by SonarQube at Blocker/Critical/Major severity
**block the push** (see [`sonarqube.md`](sonarqube.md)).

## Related

[`owasp-security.md`](owasp-security.md) · [`dotnet-security.md`](dotnet-security.md) · [`cloud.md`](cloud.md) · [`sonarqube.md`](sonarqube.md)
