namespace AjBoilerplate.Application.Idempotency;

/// <summary>What the caller of <see cref="IIdempotencyService.BeginAsync"/> should do next.</summary>
public enum IdempotencyDecision
{
    /// <summary>The key is newly claimed by this request. Execute it, then report the outcome
    /// through <see cref="IIdempotencyService.CompleteAsync"/> or
    /// <see cref="IIdempotencyService.AbandonAsync"/>.</summary>
    Proceed,

    /// <summary>This key has already produced a response. Return the stored one; do NOT execute.</summary>
    Replay,

    /// <summary>An earlier request with this key is still running. There is no response to return and
    /// no safe way to run the work twice, so the caller is refused and invited to retry.</summary>
    InProgress,

    /// <summary>The key was first used for a DIFFERENT request. Refuse: replaying would silently
    /// discard this request, and executing would break the guarantee the key asked for.</summary>
    RequestMismatch,
}

/// <summary>
/// The outcome of claiming an idempotency key, plus the stored response when the decision is
/// <see cref="IdempotencyDecision.Replay"/>.
/// </summary>
/// <param name="Decision">What the caller should do.</param>
/// <param name="StatusCode">The stored response status; meaningful only for a replay.</param>
/// <param name="ContentType">The stored response media type; meaningful only for a replay.</param>
/// <param name="Location">The stored response's <c>Location</c> header, if it had one; meaningful
/// only for a replay.</param>
/// <param name="Body">The stored response body, verbatim; meaningful only for a replay.</param>
public sealed record IdempotencyClaim(
    IdempotencyDecision Decision,
    int StatusCode,
    string? ContentType,
    string? Location,
    byte[] Body)
{
    public static IdempotencyClaim Proceed() => new(IdempotencyDecision.Proceed, 0, null, null, []);

    public static IdempotencyClaim InProgress() => new(IdempotencyDecision.InProgress, 0, null, null, []);

    public static IdempotencyClaim RequestMismatch() => new(IdempotencyDecision.RequestMismatch, 0, null, null, []);

    public static IdempotencyClaim Replay(int statusCode, string? contentType, string? location, byte[] body) =>
        new(IdempotencyDecision.Replay, statusCode, contentType, location, body);
}
