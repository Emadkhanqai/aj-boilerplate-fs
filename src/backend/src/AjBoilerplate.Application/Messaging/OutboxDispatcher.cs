using AjBoilerplate.Application.Abstractions;

namespace AjBoilerplate.Application.Messaging;

/// <summary>A failed outbox row, shaped for an operator-facing poison-message view.</summary>
public sealed record OutboxMessageView(int Id, string MessageType, int AttemptCount, string? Error, DateTime OccurredAtUtc);

/// <summary>Drains pending outbox rows to the outbound transport, and backs the retry surface.</summary>
public interface IOutboxDispatcher
{
    /// <summary>Attempts one batch; returns how many messages were attempted.</summary>
    Task<int> DispatchPendingAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxMessageView>> GetFailedAsync(CancellationToken cancellationToken);

    Task ResetForRetryAsync(int id, CancellationToken cancellationToken);
}

/// <summary>
/// Drains a batch of pending <see cref="Domain.Messaging.OutboxMessage"/> rows to
/// <see cref="IIntegrationEventPublisher"/>, driven by <c>OutboxDispatcherHostedService</c> (Api
/// layer) on a fixed timer.
/// </summary>
public sealed class OutboxDispatcher : IOutboxDispatcher
{
    private const int DefaultBatchSize = 50;

    private readonly IOutboxRepository _repository;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly IClock _clock;

    public OutboxDispatcher(IOutboxRepository repository, IIntegrationEventPublisher publisher, IClock clock)
    {
        _repository = repository;
        _publisher = publisher;
        _clock = clock;
    }

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var batch = await _repository.GetPendingBatchAsync(DefaultBatchSize, cancellationToken);

        foreach (var message in batch)
        {
            try
            {
                await _publisher.PublishAsync(
                    message.MessageType, message.PayloadJson, message.EventVersion, message.CorrelationId, cancellationToken);
                message.MarkDispatched(_clock.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single message's publish failure must never abort the batch — record it on that
                // message's own row and continue with the rest.
                message.MarkFailed(ex.Message);
            }
        }

        // One SaveChangesAsync for the whole batch: every message mutated above is already tracked by
        // the scoped DbContext behind IOutboxRepository (it was loaded, not re-fetched), and no
        // exception escapes this loop to leave a half-applied unit of work — failures are recorded on
        // the entity, not thrown.
        await _repository.SaveChangesAsync(cancellationToken);

        return batch.Count;
    }

    public async Task<IReadOnlyList<OutboxMessageView>> GetFailedAsync(CancellationToken cancellationToken)
    {
        var rows = await _repository.GetFailedAsync(cancellationToken);
        return rows.Select(m => new OutboxMessageView(m.Id, m.MessageType, m.AttemptCount, m.Error, m.OccurredAtUtc)).ToList();
    }

    public async Task ResetForRetryAsync(int id, CancellationToken cancellationToken)
    {
        var message = await _repository.FindByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Outbox message {id} was not found.");

        message.ResetForRetry();
        await _repository.SaveChangesAsync(cancellationToken);
    }
}
