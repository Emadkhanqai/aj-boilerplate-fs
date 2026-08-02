# Workflow: Code Review

> **Model routing (do first):** see [`../model-routing.md`](../model-routing.md). Final
> pre-push / architecture review → frontier tier.

Run before proposing a push, after the build and test gate, alongside SonarQube. The
slash-command form is [`/review`](../commands/review.md); the agent form is
[`../agents/code-reviewer.md`](../agents/code-reviewer.md).

## Checklist

**Architecture**
- [ ] Backend dependency directions correct (Domain → nothing; Application → Domain;
      Infrastructure → Application + Domain; Api → Application + Infrastructure + Contracts;
      Contracts = DTOs only).
- [ ] Frontend Nx module boundaries respected; `shared/util` imports nothing; no feature
      imports another feature.

**Standards**
- [ ] Matches the relevant files in [`../standards/`](../standards/).
- [ ] No `any` (TypeScript); nullable enabled and warnings-as-errors respected (C#).
- [ ] Components under ~300 lines; standalone + OnPush + signals + `inject()`.

**Correctness**
- [ ] Implements what the spec in `docs/specs/` actually says — and only that.
- [ ] Every invariant the spec states is enforced *and* tested.
- [ ] State transitions guarded; illegal transitions rejected.

**Security**
- [ ] Deny-by-default policy on every endpoint.
- [ ] Object ownership validated **after** loading the resource (IDOR/BOLA).
- [ ] Restricted fields removed by **DTO projection**, not hidden in the UI — with a test
      proving the field is absent from the serialized payload.
- [ ] No secrets, no real hostnames, project ids, or credentials.
- [ ] Errors leak no stack trace, SQL, or internal detail.

**API contract**
- [ ] Versioned route; `ApiResponse<T>` envelope with `traceId`.
- [ ] Correct status code per the table (hide-as-404, 409-for-concurrency, 410-vs-404).
- [ ] OpenAPI and the `docs/api/` snapshot updated; generated frontend types regenerated.

**Data**
- [ ] Migration present, business-named, hand-reviewed, and **not** an edit to an applied one.
- [ ] `decimal` with explicit precision for money; UTC timestamps; explicit string lengths.
- [ ] `AsNoTracking` on reads; pagination with a server-side cap; no N+1.
- [ ] Concurrency token where concurrent edits are possible.

**Forbidden patterns**
- [ ] No `EnsureCreated`, no manual DDL.
- [ ] No hand-written frontend HTTP client, no hand-duplicated DTO.
- [ ] No native HTML control where PrimeNG is required.
- [ ] No `bypassSecurityTrust*` / `innerHTML` on user content.

**Tests**
- [ ] New and changed behaviour covered; architecture tests updated.
- [ ] A negative authorization test exists.
- [ ] Playwright journey and axe-core run for any new route.

## Output

Prioritised findings, most severe first, each with `file:line` and marked **blocker** or
**nit**. No push approval while a correctness, security, or architecture blocker is open.
