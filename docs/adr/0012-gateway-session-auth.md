# ADR-0012: Sign-in is a gateway session, not a browser token

**Status:** Accepted
**Date:** 2026-11-24
**Deciders:** Boilerplate maintainers; the repository owner decided the deployment shape (see Decision).
**Supersedes:** the identity part of [ADR-0002](0002-dual-cloud-provider-behind-one-switch.md) only. The rest of ADR-0002 (secrets behind `CLOUD_PROVIDER`, one `infra/` tree per cloud) stands.

---

## Context

Until 1.0 the Angular app was an OIDC client. It signed users in against the cloud's own identity
service (Google Cloud Identity on `gcp`, Microsoft Entra ID on `azure`), kept the resulting tokens
in the browser, refreshed them from the browser, and attached them to API calls. The API accepted
either that cloud token directly or a Keycloak token. ADR-0002 recorded this as one of the two
places the code branches on the cloud.

Three things made that a poor default for the projects that start from this boilerplate:

1. **Tokens in the browser.** A token in `localStorage` is readable by any script that runs in the
   page. That is the property a content-security policy and a long list of review rules try to
   defend, and it is a standing risk for an internal business application.
2. **Two token shapes and a branch in code.** The API had a Keycloak scheme and a "cloud identity
   directly" scheme. Roles only existed in the first. Two ways in is two things to test and one
   more thing to get wrong.
3. **A platform already solved this.** The Al Jazeera USP platform runs a different model on its
   staging environment: a gateway nginx, a shared auth-backend that signs users in on the server
   with Entra ID, and an RFC 8693 token exchange at Keycloak. Projects inside USP will sit behind
   that gateway; projects outside USP want the same security properties without USP.

## Decision

**The browser holds one opaque, `HttpOnly` session cookie and no token.** The model is a port of
the USP staging contract, kept in [`docs/auth/gateway-contract.md`](../auth/gateway-contract.md):

- A new .NET host, **`AjBoilerplate.Auth`**, signs users in with a server-side Authorization Code
  + PKCE flow against Entra ID, exchanges the Entra token at Keycloak (RFC 8693) for a JWT minted
  for this application's own client, and keeps the session in SQL Server with Redis in front,
  tokens encrypted with AES-256-GCM. Sessions end after 8 h idle or 12 h absolute.
- The **web container's nginx is the gateway**. It runs `auth_request` against the Auth service,
  takes the JWT from the answer and forwards it to the API as `Authorization: Bearer`.
- The **API is a plain Keycloak resource server**: signing keys from JWKS, an `azp` check instead
  of audience validation, roles from client roles and a flat `roles` claim, policies satisfied by
  a seat **or** a permission, and `GET /api/v1/me` as the single source of the user's capabilities.
- The **SPA has no token and no OIDC client.** Sign-in is a full-page navigation to `/auth/login`.
- **Header names are kept identical to USP's** (`X-Usp-*`), so a project can move between the two
  providers without changing portal code.

**One switch decides who runs the gate: `AUTH_PROVIDER=embedded|usp`.**

- `embedded` (default): the project deploys its **own** Auth service, one per project. This is
  the choice for every project outside the Al Jazeera USP platform.
- `usp`: the project sits behind USP's shared gateway and auth-backend and **does not deploy the
  Auth service**. The web container serves static files only.

A second switch, `AUTH_MODE=gateway|dev`, keeps the offline role picker for UI work and demos.
It cannot call a gateway-mode API.

Defaults equal USP staging's Entra values (scopes `openid profile offline_access User.Read`,
`RequestOwnClientIdScope=false`, `SubjectIssuer=oidc`), with the reason for each recorded beside
the option. Azure AD B2C stays reachable by configuration.

**Removed:** the browser OIDC provider and every stored token; the `Authentication:GoogleCloudIdentity`
and `Authentication:EntraId` API schemes; `Keycloak:Realm`, `Keycloak:ClientSecret` and the
`OIDC_*` runtime variables. `CLOUD_PROVIDER` now selects secrets and the `infra/` tree only.

### Enforced where it can be

- The Auth service references none of the layered API projects and none reference it; an
  architecture test fails the build otherwise.
- The API refuses to start outside Development on a plaintext JWKS URI (unless explicitly
  accepted with `Keycloak:AllowInsecureJwksTransport`), or on an unknown `Auth:Mode`.
- The Auth service validates its configuration at start-up and refuses insecure combinations
  outside Development: http cookies, http upstreams, an empty `SubjectIssuer`.

## Consequences

### Positive

- No credential is reachable from page script. A cross-site-scripting bug can make requests as
  the user while the tab is open; it cannot exfiltrate a token that outlives the tab.
- One token shape reaches the API, so one path to test, and authorisation is identical on both
  clouds and inside USP.
- Revocation is real: a Keycloak role removal ends access within the JWT lifetime, and the SPA
  tells the user (access-revoked dialog) rather than failing mysteriously.
- A project can move between `embedded` and `usp` with configuration, not code.
- The infrastructure-fault rules in the contract mean a Keycloak or Redis outage degrades to 503
  rather than signing everyone out.

### Negative

- **More to run.** A project outside USP deploys the web gateway, an Auth service, an Auth database
  schema and Redis, and needs a Keycloak that can exchange an Entra token. `infra/gcp` deploys the
  Auth service, its migration job and the web container against an existing Keycloak it never
  deploys (not yet applied against a project); `infra/azure` deploys none of them, and on Azure that
  is the adopting project's work.
- **The real-Entra path is not exercised in this repository.** The local stack replaces Entra
  with a second Keycloak client and uses Keycloak's standard (internal) token exchange. The
  external-token exchange against a real tenant is untested here.
- **A cookie now exists.** The API itself never reads it (nginx blanks it and sends a bearer
  token), the cookie is `SameSite=Lax` and `HttpOnly`, and logout is a same-origin `POST`. The
  API therefore still ships no antiforgery middleware, and this ADR is the record that
  `.claude/standards/middleware.md` asks for when an auth cookie is introduced.
- **Coupling to USP's contract.** The header names and flow follow another team's platform. Where
  that platform changes, this contract does not follow automatically.
- The nginx gateway's behaviour is part of the security boundary, so `docker/nginx/` needs the same
  review as application code.

### Neutral

- `ADR-0002`'s "identity branches on the cloud" no longer holds; see the note at its top.
- Local sign-in uses a bundled Keycloak realm with committed, dev-only secrets.
- The API records each sign-in once in an `AuthenticationEvents` table. It is a sign-in log, not an
  audit trail (see the note on [ADR-0008](0008-no-audit-trail-or-soft-delete.md)).

### Follow-on work

- Deployment of the Auth service and web gateway on Azure (Bicep). Done on Google Cloud, in
  `infra/gcp` and `deploy.yml`, and not yet applied against a project.
- Exercising the real-Entra and external-exchange path against a test tenant.

## Alternatives considered

### Keep client-side OIDC

Smallest change, and works with no extra service. Rejected: tokens stay in the browser, the API
keeps two token shapes, and revocation depends on the browser cooperating.

### A BFF inside the API process

Put the sign-in endpoints and session store in `AjBoilerplate.Api`. Fewer deployables. Rejected:
it welds session handling into the layered API, whose dependency rules (`Api` must not reference
`Domain`, and so on) are about business code; it ties the Auth lifecycle to the API's release
cadence; and `AUTH_PROVIDER=usp` would then mean an API that carries dead sign-in code.

### A separate repository for the Auth service

Good for reuse. Rejected by the repository owner: the decision was one Auth service per project
outside USP and the shared USP auth-backend inside it. A separate **project in the same
repository** (`AjBoilerplate.Auth`) gives independence, enforced by an architecture test, without
a fourth repository to version and keep in step with the three that ADR-0006 and ADR-0011 already publish.

### Make "embedded vs USP" a code branch

Compile in both. Rejected: it is a configuration switch (`AUTH_PROVIDER`) because the difference is
who runs the gateway, not what the application does.

### Put the gateway rules somewhere other than the web container

A separate nginx image or the platform load balancer. Rejected for `embedded`: the web container
already is an nginx in front of the API, so the rules live there as three profiles chosen at
container start. Inside USP the shared gateway plays this role and the `usp` profile gets out of
its way.

### Doing nothing

Rejected: see Context.

## Verification

- `dotnet test src/backend/tests/AjBoilerplate.Auth.Tests` and the Architecture tests cover the
  Auth service and its independence; the API's identity tests cover seats, permissions and `azp`.
- `docker compose up` runs the whole flow with the bundled realm.
- A signal that should trigger a superseding ADR: USP changes its contract, or a project needs a
  second identity provider alongside Entra.

## References

- [Gateway contract](../auth/gateway-contract.md) · [Setup](../auth/setup.md) · [Troubleshooting](../auth/troubleshooting.md)
- [ADR-0002](0002-dual-cloud-provider-behind-one-switch.md) (the identity part is superseded)
- [ADR-0006](0006-three-repository-split.md) · [ADR-0011](0011-scripted-one-way-derivation-for-the-three-repositories.md)
- RFC 8693, OAuth 2.0 Token Exchange
