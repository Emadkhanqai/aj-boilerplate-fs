using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Persistence;
using AjBoilerplate.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Application.Idempotency;

/// <summary>
/// Claims and releases idempotency keys. The Api layer's middleware drives this; every parameter and
/// return value is a primitive or an Application-owned type, so the Api never touches the Domain
/// assembly — the architecture rule <c>DependencyRuleTests</c> enforces.
/// </summary>
public interface IIdempotencyService
{
    /// <summary>
    /// Claims <paramref name="key"/> for <paramref name="scope"/> and says what the caller should do.
    /// </summary>
    /// <param name="scope">The authenticated caller's identity. Never a client-supplied value.</param>
    /// <param name="key">The <c>Idempotency-Key</c> header value.</param>
    /// <param name="method">The request method, for the request digest and for diagnostics.</param>
    /// <param name="path">The request path, for the request digest and for diagnostics.</param>
    /// <param name="requestHash">A digest of the request this key is being used for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IdempotencyClaim> BeginAsync(
        string scope, string key, string method, string path, string requestHash, CancellationToken cancellationToken);

    /// <summary>Stores the response the claimed request produced, so a replay returns it.</summary>
    Task CompleteAsync(
        string scope, string key, int statusCode, string? contentType, string? location, byte[] body,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases a claim whose request produced nothing worth replaying, so the key stays usable.
    /// </summary>
    Task AbandonAsync(string scope, string key, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IIdempotencyService"/>
public sealed class IdempotencyService : IIdempotencyService
{
    private readonly IIdempotencyRepository _records;
    private readonly IClock _clock;

    public IdempotencyService(IIdempotencyRepository records, IClock clock)
    {
        _records = records;
        _clock = clock;
    }

    public async Task<IdempotencyClaim> BeginAsync(
        string scope, string key, string method, string path, string requestHash, CancellationToken cancellationToken)
    {
        var existing = await _records.FindAsync(scope, key, cancellationToken);

        if (existing is not null)
        {
            return Decide(existing, requestHash);
        }

        var record = IdempotencyRecord.Start(
            Guid.NewGuid(), scope, key, method, path, requestHash, _clock.UtcNowOffset);
        await _records.AddAsync(record, cancellationToken);

        try
        {
            await _records.SaveChangesAsync(cancellationToken);
            return IdempotencyClaim.Proceed();
        }
        catch (DbUpdateException ex) when (SqlUniqueConstraintViolation.Matches(ex))
        {
            // THE RACE THIS FEATURE EXISTS FOR. Two requests carrying the same key both found no
            // record above and both tried to insert; the unique index on (Scope, Key) admits exactly
            // one. Losing here is not an error — it means someone else claimed the key first, so
            // re-read and answer from THEIR record.
            //
            // The rejected INSERT is still tracked in the Added state, so the context must be cleared
            // before it is used again or the next save would retry the same doomed statement.
            _records.ClearChangeTracking();

            var winner = await _records.FindAsync(scope, key, cancellationToken);

            // The winner can legitimately be gone already: a fast first attempt could have failed and
            // abandoned its claim in the moment between the violation and this re-read. Treating that
            // as "in progress" asks the client to retry, which is exactly right — it must not execute
            // here, because this request's own INSERT was rejected and it holds no claim.
            return winner is null ? IdempotencyClaim.InProgress() : Decide(winner, requestHash);
        }
    }

    public async Task CompleteAsync(
        string scope, string key, int statusCode, string? contentType, string? location, byte[] body,
        CancellationToken cancellationToken)
    {
        var record = await _records.FindAsync(scope, key, cancellationToken);

        // Gone, or already completed by something else: there is nothing to record and nothing worth
        // failing the caller's request over — the work itself succeeded and its response is already
        // on its way out.
        if (record is null || record.Status == IdempotencyRecordStatus.Completed)
        {
            return;
        }

        record.Complete(statusCode, contentType, location, body, _clock.UtcNowOffset);
        await _records.SaveChangesAsync(cancellationToken);
    }

    public async Task AbandonAsync(string scope, string key, CancellationToken cancellationToken)
    {
        var record = await _records.FindAsync(scope, key, cancellationToken);

        // Only an unfinished claim is releasable. A completed record is the stored response and must
        // survive — dropping it would turn a successful create into one that can be replayed into a
        // second create.
        if (record is null || record.Status == IdempotencyRecordStatus.Completed)
        {
            return;
        }

        _records.Remove(record);
        await _records.SaveChangesAsync(cancellationToken);
    }

    private static IdempotencyClaim Decide(IdempotencyRecord record, string requestHash)
    {
        // Checked BEFORE the status: a key reused for different work is a client bug whatever state
        // the original request reached, and answering "still in progress" would send that client into
        // a retry loop for a request that will never be accepted under this key.
        if (!record.Matches(requestHash))
        {
            return IdempotencyClaim.RequestMismatch();
        }

        return record.Status == IdempotencyRecordStatus.Completed
            ? IdempotencyClaim.Replay(
                record.ResponseStatusCode ?? 0, record.ResponseContentType, record.ResponseLocation,
                record.ResponseBody ?? [])
            : IdempotencyClaim.InProgress();
    }
}
