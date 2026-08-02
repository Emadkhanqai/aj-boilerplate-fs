using AjBoilerplate.Domain.Messaging;

namespace AjBoilerplate.Application.Abstractions;

/// <summary>Port for reading and persisting transactional-outbox rows. Implemented in
/// Infrastructure.</summary>
public interface IOutboxRepository
{
    /// <summary>Up to <paramref name="maxCount"/> <see cref="OutboxMessageStatus.Pending"/> messages,
    /// oldest <c>OccurredAtUtc</c> first (matches the <c>(Status, OccurredAtUtc)</c> index), for a
    /// dispatcher to attempt in order.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetPendingBatchAsync(int maxCount, CancellationToken cancellationToken);

    /// <summary>Every <see cref="OutboxMessageStatus.Failed"/> row, newest first, for an
    /// operator-facing poison-message/retry view.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetFailedAsync(CancellationToken cancellationToken);

    /// <summary>A single outbox row by id, or null — the retry action's lookup.</summary>
    Task<OutboxMessage?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task AddAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
