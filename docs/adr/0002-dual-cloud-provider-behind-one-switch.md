# ADR-0002: Two cloud providers behind a single `CLOUD_PROVIDER` switch

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Boilerplate maintainers

---

## Context

Different projects starting from this boilerplate will land on different clouds, and some will
be told which cloud only after the code exists. A boilerplate that hard-codes one provider is
useless to half its audience; a boilerplate that abstracts every provider service behind a
lowest-common-denominator interface is useless to everyone.

Looking at what the application actually needs from a cloud, the surface is much smaller than
it first appears:

| Concern | Genuinely provider-specific? |
|---|---|
| Secrets | **Yes** — Secret Manager and Key Vault have different SDKs and different auth |
| Identity (authN) | **Yes** — different issuer, different token validation configuration |
| Authorization | No — Keycloak, self-hosted, identical either way |
| Cache | No — Memorystore and Cache for Redis both speak the Redis protocol |
| Database | No — SQL Server either way, same connection string shape |
| Container runtime | No — the application does not know it is in Cloud Run or Container Apps |
| Infrastructure | **Yes**, entirely — but that is IaC, not application code |

Only two concerns need a branch in code. Everything else differs in configuration or in the
`infra/` tree, neither of which the running application observes.

## Decision

We will support `gcp` and `azure`, selected by a single environment variable `CLOUD_PROVIDER`
(bound to `Cloud:Provider` in configuration), and we will confine the branch to two places at
the composition root:

1. **Secrets** — `ISecretsProvider`, implemented by `GcpSecretManagerSecretsProvider` and
   `AzureKeyVaultSecretsProvider`, registered by the switch. No other code knows which is live.
2. **Authentication** — the JWT bearer configuration selects the Google Cloud Identity or
   Microsoft Entra ID issuer, metadata address, and audience.

Authorization stays in Keycloak for both, so roles, policies, and claims mapping are written
once. Redis is configured by connection string only. `deploy.yml` reads the same switch as a
repository variable and runs `infra/gcp/` (Terraform) or `infra/azure/` (Bicep).

There is no default value. An unset or unrecognised `CLOUD_PROVIDER` fails at startup with a
clear message rather than guessing.

Adding a third provider means adding one `ISecretsProvider` implementation, one authentication
branch, and one `infra/` tree — and nothing else. If a change ever requires a provider check
outside those two places, the abstraction is wrong and this ADR should be revisited.

## Consequences

### Positive

- A project can be told "actually, it's the other cloud" late and pay two configuration changes
  rather than a rewrite.
- The provider-specific surface is small enough to hold in your head, and it is all in one
  directory.
- Local development can run against either configuration, so neither path rots.
- Keycloak for authorization means authorization logic and tests are provider-independent, which
  is where most of the complexity actually lives.

### Negative

- Both `infra/` trees must be maintained, and the one your project does not use will drift. We
  accept this: consuming projects are expected to delete the tree they are not using on day one.
- Two cloud SDKs are referenced by `Infrastructure`, so both are restored and both appear in
  vulnerability scans, even though only one is ever loaded.
- Provider-specific capabilities that would genuinely be *better* — a native managed identity
  flow, a provider-integrated queue — are harder to adopt without breaking the symmetry.
- Only one path is exercised in any given deployment, so a bug in the other is found late.

### Neutral

- CI must know the provider, which means a repository variable and two sets of OIDC secrets.

### Follow-on work

- Integration coverage should construct both `ISecretsProvider` registrations so a broken
  registration is caught even on the unused path.

## Alternatives considered

### Pick one cloud

Simplest, and the right answer for a product. Rejected for a boilerplate, whose value is
precisely that it does not force this decision on its consumer.

### Abstract everything behind provider-neutral interfaces

A full portability layer over storage, queues, identity, and observability. Rejected: the
abstraction would be larger than the application, would expose only the intersection of both
platforms, and would have to be maintained forever. The analysis above shows the truly divergent
surface is two interfaces wide.

### Environment-specific forks of the repository

Rejected immediately — two divergent codebases from day one is the outcome this decision exists
to prevent.

### A build-time compilation symbol instead of a runtime switch

Would produce a smaller binary with only one SDK. Rejected because it makes the choice a build
artefact: you can no longer run the same image in two environments, and CI must produce two
images to test both paths.

## Verification

Startup fails loudly on an unset or unknown `CLOUD_PROVIDER`. Grep for the provider name outside
`Infrastructure/Secrets/`, the authentication setup, and `infra/` — any hit means the abstraction
has leaked, and is grounds for a superseding ADR.

## References

- [infra/gcp/README.md](../../infra/gcp/README.md) · [infra/azure/README.md](../../infra/azure/README.md)
- [.github/workflows/deploy.yml](../../.github/workflows/deploy.yml)
