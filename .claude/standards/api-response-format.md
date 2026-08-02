# Standard: Standard API Response Format

Every API response — success or failure — uses a shared response envelope, so clients get a
predictable shape with a correlation id and stable machine-readable error codes.

## The envelope

Lives in `AjBoilerplate.Contracts.Common`.

```csharp
using System.Text.Json.Serialization;

namespace AjBoilerplate.Contracts.Common;

public sealed class ApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool IsSuccess { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<string>? Errors { get; init; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; } = 200;

    /// <summary>Stable, machine-readable error code. Never repurposed once published.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    public static ApiResponse<T> Success(T data, string? message = null, int statusCode = 200)
        => new() { IsSuccess = true, Data = data, Message = message, StatusCode = statusCode };

    public static ApiResponse<T> Failure(
        string message,
        int statusCode = 400,
        string? code = null,
        IReadOnlyList<string>? errors = null)
        => new() { IsSuccess = false, Message = message, Code = code, Errors = errors, StatusCode = statusCode };
}

/// <summary>Non-generic variant for responses with no payload.</summary>
public sealed class ApiResponse
{
    [JsonPropertyName("success")]
    public bool IsSuccess { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<string>? Errors { get; init; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; } = 200;

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    public static ApiResponse Success(string? message = null, int statusCode = 200)
        => new() { IsSuccess = true, Message = message, StatusCode = statusCode };

    public static ApiResponse Failure(
        string message,
        int statusCode = 400,
        string? code = null,
        IReadOnlyList<string>? errors = null)
        => new() { IsSuccess = false, Message = message, Code = code, Errors = errors, StatusCode = statusCode };
}
```

Paged reads use `PagedResponse<T>` carried as the `data` of an `ApiResponse<PagedResponse<T>>`:

```csharp
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
```

## Rules

- **Every successful API response uses `ApiResponse<T>`** (or `ApiResponse` when there is no
  payload).
- **Every failed API response uses `ApiResponse<T>` / `ApiResponse`.** No bespoke error shapes.
- **`traceId` is always populated** from `Activity.Current?.Id ?? HttpContext.TraceIdentifier`
  — set centrally in the response-wrapping result filter and the exception handler chain, not
  per controller.
- **Validation errors go in `errors`** (a flat list of human-readable messages); `code` carries
  the stable machine-readable error code.
- **`code` is stable and documented** (e.g. `VALIDATION_FAILED`, `NOT_FOUND`, `CONFLICT`,
  `FORBIDDEN`, `RESOURCE_GONE`, `INTERNAL_ERROR`). Once published, a code is never repurposed.
- **Never expose stack traces, SQL errors, or internal exception messages.** In production an
  unexpected error returns a generic message + `code` + `traceId`; the detail goes to logs and
  telemetry only (see [`error-handling.md`](error-handling.md)).

## HTTP status codes — the full table

Applies to every endpoint. The `code` column is the value in the envelope's `code` field.

| Status | When to use | Envelope `code` (example) | Body |
|---|---|---|---|
| **200 OK** | Successful read, or an update that returns the new state | — | `ApiResponse<T>` with `data` |
| **201 Created** | Resource created. **Must** set the `Location` header to the new resource | — | `ApiResponse<T>` with the created resource |
| **202 Accepted** | Work accepted for asynchronous processing; not yet done. Return a status/polling URL | — | `ApiResponse<T>` describing the accepted job |
| **204 No Content** | Success with deliberately no body (e.g. `DELETE`) | — | *(empty — no envelope)* |
| **400 Bad Request** | Malformed request or failed validation | `VALIDATION_FAILED` | `errors[]` holds per-field messages |
| **401 Unauthorized** | Missing, expired, or invalid credentials. The caller is *unauthenticated* | `UNAUTHENTICATED` | Generic message only |
| **403 Forbidden** | Authenticated but not permitted — **and the caller is allowed to know the resource exists** | `FORBIDDEN` | Generic message only |
| **404 Not Found** | Resource does not exist — **or exists but the caller must not learn that it does** (see below) | `NOT_FOUND` | Generic message only |
| **409 Conflict** | State conflict: duplicate unique key, invalid state transition, **or an optimistic-concurrency failure** | `CONFLICT`, `CONCURRENCY_CONFLICT` | Safe, business-meaningful message |
| **410 Gone** | The resource *did* exist and is permanently, deliberately withdrawn | `RESOURCE_GONE` | Safe message |
| **500 Internal Server Error** | Unhandled fault. Never leaks detail | `INTERNAL_ERROR` | Generic message + `traceId` |
| **503 Service Unavailable** | Dependency down, or maintenance mode. Send `Retry-After` when known | `SERVICE_UNAVAILABLE` | Generic message |

Also in use: **429 Too Many Requests** (`RATE_LIMITED`) from the rate limiter — see
[`middleware.md`](middleware.md).

### Convention: hide-as-404

If revealing that a resource exists would itself leak information the caller is not entitled
to, return **404, not 403**. Use 403 only when the caller is entitled to know the resource
exists but not to act on it. Decide this per resource and write it down in the endpoint's
OpenAPI description — inconsistency here is an information-disclosure bug.

### Convention: 409 for optimistic concurrency

Concurrency tokens (`rowversion`) surface to the client as an `ETag`; updates carry
`If-Match`. A mismatch — the row changed since the caller read it — returns **409** with
`code = CONCURRENCY_CONFLICT` and a message telling the user to reload. Do **not** silently
overwrite, and do not use 412 for this case unless the endpoint is documented as
precondition-based; pick one and apply it consistently across the API.

### Convention: 410 vs 404

- **404** — "no such resource, as far as you are concerned." The default. Use it for unknown
  ids, deleted-and-forgotten rows, and hide-as-404 cases.
- **410** — "this existed, it is permanently gone, stop asking." Use it only where the client
  benefits from the distinction: expired one-time links, retired API resources, purged
  records. 410 is an admission that the identifier was once valid, so never use it where that
  admission is itself a leak.

## Interop with ProblemDetails

RFC 9457 / RFC 7807 `ProblemDetails` remains valid for framework-level failures (model
binding, routing 404s). Where both apply, prefer the `ApiResponse` envelope for
application/business responses and keep `ProblemDetails` for pipeline/framework faults —
ensuring `traceId` appears in both. Document both shapes in OpenAPI (see
[`swagger-openapi.md`](swagger-openapi.md)).

## Related

[`error-handling.md`](error-handling.md) · [`middleware.md`](middleware.md) · [`observability-tracing.md`](observability-tracing.md) · [`api-versioning.md`](api-versioning.md)
