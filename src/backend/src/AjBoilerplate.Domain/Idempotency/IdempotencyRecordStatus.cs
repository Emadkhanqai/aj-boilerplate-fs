namespace AjBoilerplate.Domain.Idempotency;

/// <summary>Lifecycle of an <see cref="IdempotencyRecord"/>.</summary>
public enum IdempotencyRecordStatus
{
    /// <summary>The key has been claimed and the request is executing. A concurrent replay arriving
    /// in this state is refused rather than executed — the first attempt's outcome is not known
    /// yet, so there is no response to return and no safe way to run the work twice.</summary>
    InProgress,

    /// <summary>The request finished and its response is stored. A replay returns it verbatim.</summary>
    Completed,
}
