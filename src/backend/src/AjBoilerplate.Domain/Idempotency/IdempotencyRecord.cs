using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Domain.Idempotency;

/// <summary>
/// The record of one unsafe request that a caller labelled with an <c>Idempotency-Key</c>, together
/// with the response it produced. A replay of the same key returns this stored response instead of
/// executing the request a second time.
///
/// <para>
/// The row is written BEFORE the request runs, in the <see cref="IdempotencyRecordStatus.InProgress"/>
/// state. That ordering is the entire mechanism: a unique index on
/// (<see cref="Scope"/>, <see cref="Key"/>) means only one concurrent request can create it, so the
/// loser of the race is told the work is already happening rather than doing it again. Writing the
/// row after the work would leave exactly the window the feature exists to close.
/// </para>
///
/// <para>
/// <see cref="Scope"/> is the caller's own identity, never a value the client supplies. Two users
/// picking the same key — a plain <c>1</c> is a realistic client bug — must not collide, and a
/// caller must never be able to read back another caller's response by guessing a key. This is a
/// privacy boundary, not a partitioning convenience.
/// </para>
///
/// Persistence-ignorant — no EF concerns here.
/// </summary>
public sealed class IdempotencyRecord : AuditedEntity
{
    /// <summary>The longest <c>Idempotency-Key</c> accepted. A UUID or a ULID fits comfortably;
    /// anything longer is a client using the header for something it is not.</summary>
    public const int MaxKeyLength = 128;

    /// <summary>The longest caller identity stored — matches the identity provider's subject
    /// identifier, as elsewhere in this schema.</summary>
    public const int MaxScopeLength = 128;

    /// <summary>Enough for the request line this record is bound to.</summary>
    public const int MaxMethodLength = 10;

    /// <summary>Longer than any route this API exposes, and bounded so a pathological URL cannot
    /// widen the row.</summary>
    public const int MaxPathLength = 400;

    /// <summary>A hex SHA-256 digest.</summary>
    public const int RequestHashLength = 64;

    /// <summary>Long enough for any media type this API returns.</summary>
    public const int MaxContentTypeLength = 100;

    /// <summary>Matches <see cref="MaxPathLength"/>: a Location header is a URL to a resource this
    /// API just created, so it is bounded by the same routes.</summary>
    public const int MaxLocationLength = 400;

    public Guid Id { get; private set; }

    /// <summary>The caller this record belongs to — the authenticated subject identifier.</summary>
    public string Scope { get; private set; }

    /// <summary>The client-supplied <c>Idempotency-Key</c> header value.</summary>
    public string Key { get; private set; }

    /// <summary>The HTTP method the key was first used with.</summary>
    public string Method { get; private set; }

    /// <summary>The request path the key was first used with.</summary>
    public string Path { get; private set; }

    /// <summary>
    /// A digest of the original request (method, path, and body). Reusing a key with a DIFFERENT
    /// request is a client bug, and the honest answer is to refuse it: replaying the first response
    /// would silently discard the second request, and executing it would break the guarantee the key
    /// was asking for. See <see cref="Matches"/>.
    /// </summary>
    public string RequestHash { get; private set; }

    public IdempotencyRecordStatus Status { get; private set; }

    /// <summary>The stored response status; null while the request is still in progress.</summary>
    public int? ResponseStatusCode { get; private set; }

    /// <summary>The stored response's media type; null while the request is still in progress.</summary>
    public string? ResponseContentType { get; private set; }

    /// <summary>
    /// The stored response body, verbatim. Bytes rather than text so a replay reproduces exactly
    /// what the first caller received, without a re-serialisation that could differ.
    /// </summary>
    public byte[]? ResponseBody { get; private set; }

    /// <summary>
    /// The stored response's <c>Location</c> header, when it had one; null otherwise.
    ///
    /// Captured explicitly rather than as part of a general header store. A <c>201 Created</c>'s
    /// Location IS part of its response — a client that follows it to read back what it created gets
    /// nothing if a replay drops it — so replaying the status and body without it would not be
    /// replaying the same response. Every other response header is either recomputed correctly by the
    /// pipeline on the replayed request (security headers, correlation) or would be actively wrong if
    /// restored from an older request, so a blanket capture would trade a real bug for a subtler one.
    /// </summary>
    public string? ResponseLocation { get; private set; }

    /// <summary>When the response was captured; null while the request is still in progress.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    private IdempotencyRecord()
    {
        Scope = Key = Method = Path = RequestHash = string.Empty;
    }

    private IdempotencyRecord(Guid id, string scope, string key, string method, string path, string requestHash)
    {
        Id = id;
        Scope = scope;
        Key = key;
        Method = method;
        Path = path;
        RequestHash = requestHash;
        Status = IdempotencyRecordStatus.InProgress;
    }

    /// <summary>Claims <paramref name="key"/> for <paramref name="scope"/> and marks the request in
    /// progress. The unique index is what makes the claim exclusive under concurrency.</summary>
    public static IdempotencyRecord Start(
        Guid id, string scope, string key, string method, string path, string requestHash, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new DomainException("An idempotency record's scope is required.");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new DomainException("An idempotency key is required.");
        }

        var record = new IdempotencyRecord(id, scope, key, method, path, requestHash);
        record.MarkCreated(nowUtc);
        return record;
    }

    /// <summary>
    /// True when <paramref name="requestHash"/> is the request this key was originally claimed for.
    /// A false answer means the caller reused a key for different work.
    /// </summary>
    public bool Matches(string requestHash) =>
        string.Equals(RequestHash, requestHash, StringComparison.Ordinal);

    /// <summary>
    /// Stores the response this request produced, so any later replay of the key returns it verbatim.
    /// </summary>
    public void Complete(int statusCode, string? contentType, string? location, byte[] body, DateTimeOffset nowUtc)
    {
        if (Status == IdempotencyRecordStatus.Completed)
        {
            // Completing twice means two executions of a request this record was supposed to make
            // happen once — a bug in the middleware, not a business-rule violation.
            throw new InvalidOperationException($"Idempotency record {Id} has already been completed.");
        }

        Status = IdempotencyRecordStatus.Completed;
        ResponseStatusCode = statusCode;
        ResponseContentType = contentType;
        ResponseLocation = location;
        ResponseBody = body;
        CompletedAt = nowUtc;
        Touch(nowUtc);
    }
}
