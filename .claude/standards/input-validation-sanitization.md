# Standard: Input Validation & Sanitization

Validate at the boundary, before any domain or persistence work. Sanitize free text where it
will be displayed. Never trust the client.

## Validation

- **Every command/request is validated with FluentValidation** before the use case runs. An
  invalid input never reaches the domain or the database.
- Validation lives in the **Application layer** (`AbstractValidator<TCommand>`) — not in
  controllers, and not *only* in the domain (the domain still guards its own invariants as the
  last line of defence).
- **Fail fast, fail specific:** return `400` with per-field messages in `errors[]` and
  `code = VALIDATION_FAILED` (see [`error-handling.md`](error-handling.md)).
- **Whitelist, don't blacklist.** Constrain types, lengths, ranges, formats, and allowed sets:
  - Numeric quantities → explicit range, and reject negatives unless negatives are meaningful.
  - Enumerations → must be a known member; reject unknown values rather than defaulting.
  - Foreign keys → must resolve to an existing row the caller is entitled to reference.
  - Free text → explicit maximum length, and reject control characters.
  - Email / URL / identifier formats → validated before any downstream use.
  - Pagination → `page >= 1`, `pageSize` within a hard server-side cap.
- **Mass-assignment safe:** requests are DTOs mapped explicitly to commands. Server-owned
  fields (`Id`, `Status`, `OwnerId`, `CreatedAt`, `UpdatedAt`, `RowVersion`, audit fields) are
  **never** bound from the client.
- **Validate before you authorize where cheap, authorize before you act always.** A validation
  message must not reveal the existence of a resource the caller cannot see.

## Sanitization

- **Sanitize or encode free-text fields where they are displayed or exported** (names, notes,
  comments, templates) as defence-in-depth against stored XSS. **Store raw, encode on output**;
  the frontend escapes as well (see [`angular.md`](angular.md)).
- Reject or neutralise control characters and oversized payloads at the boundary.
- Treat file uploads as hostile: validate content type by sniffing, not by extension; cap size;
  store outside the web root; never execute.
- Validate any outbound URL against an allow-list before fetching it (SSRF).

## Boundary placement

- Validation/sanitization is a **pipeline boundary** concern: it sits at the Application entry
  and is enforced before domain logic (see [`middleware.md`](middleware.md)).
- The domain re-checks its invariants regardless — validation is not a substitute for domain
  guards, and a domain object must not be constructible in an invalid state.

## Related

[`error-handling.md`](error-handling.md) · [`owasp-security.md`](owasp-security.md) · [`dotnet-security.md`](dotnet-security.md) · [`api-response-format.md`](api-response-format.md)
