using AjBoilerplate.Domain.Idempotency;

namespace AjBoilerplate.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="IdempotencyRecord"/>. Deliberately narrow: the interesting behaviour
/// is the unique-index race, which lives in <c>IdempotencyService</c>, not here.
/// </summary>
public interface IIdempotencyRepository
{
    /// <summary>The record claiming <paramref name="key"/> for <paramref name="scope"/>, or null.</summary>
    Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken);

    Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken);

    /// <summary>Drops a record whose request produced nothing worth replaying, so the key stays
    /// usable for a genuine retry.</summary>
    void Remove(IdempotencyRecord record);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forgets everything the context is tracking. Needed after a failed <c>SaveChanges</c>: the
    /// rejected entity stays tracked in the Added state, so the very next save would retry the same
    /// doomed INSERT. Mirrors <c>IInboxRepository.ClearChangeTracking</c>, which exists for the same
    /// reason.
    /// </summary>
    void ClearChangeTracking();
}
