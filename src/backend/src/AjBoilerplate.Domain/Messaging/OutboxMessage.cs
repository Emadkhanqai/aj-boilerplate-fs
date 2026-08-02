namespace AjBoilerplate.Domain.Messaging;

/// <summary>
/// A minimal transactional-outbox row: an integration event written in the same database
/// transaction as the domain change that raised it, so a durable dispatcher can pick it up and
/// deliver it exactly-once-ish without a two-phase commit. Persistence-ignorant — the dispatcher
/// (Application/Infrastructure) owns reading, sending, and calling back into these transition
/// methods; this class only enforces the state-machine invariants.
/// </summary>
public sealed class OutboxMessage
{
    private const int MaxErrorLength = 2000;

    public int Id { get; }

    public string MessageType { get; private set; }

    public string PayloadJson { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public DateTime? ProcessedAtUtc { get; private set; }

    public OutboxMessageStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public string? Error { get; private set; }

    public int EventVersion { get; private set; } = 1;

    /// <summary>The correlation id of the business action that raised this event (the HTTP request's
    /// trace id — see <c>ICorrelationContext</c>), carried through so a consumer, or this row's own
    /// audit trail, can be joined back to the originating request without deserializing
    /// <see cref="PayloadJson"/>. Null for messages raised outside an HTTP request context.</summary>
    public string? CorrelationId { get; private set; }

    private OutboxMessage()
    {
        MessageType = PayloadJson = string.Empty;
    }

    private OutboxMessage(string messageType, string payloadJson, DateTime occurredAtUtc, int eventVersion, string? correlationId)
    {
        MessageType = messageType;
        PayloadJson = payloadJson;
        OccurredAtUtc = occurredAtUtc;
        Status = OutboxMessageStatus.Pending;
        AttemptCount = 0;
        ProcessedAtUtc = null;
        EventVersion = eventVersion;
        CorrelationId = correlationId;
    }

    public static OutboxMessage Create(string messageType, string payloadJson, DateTime occurredAtUtc,
        int eventVersion = 1, string? correlationId = null) =>
        new(messageType, payloadJson, occurredAtUtc, eventVersion, correlationId);

    /// <summary>Mark this message successfully dispatched. Throws if already dispatched — calling
    /// this twice is a bug in the dispatcher, not a business-rule violation, hence
    /// <see cref="InvalidOperationException"/> rather than a domain exception.</summary>
    public void MarkDispatched(DateTime nowUtc)
    {
        if (Status == OutboxMessageStatus.Dispatched)
        {
            throw new InvalidOperationException($"Outbox message {Id} has already been dispatched.");
        }

        Status = OutboxMessageStatus.Dispatched;
        ProcessedAtUtc = nowUtc;
        Error = null;
    }

    /// <summary>Record a failed dispatch attempt. Safe to call repeatedly — each call increments the
    /// attempt count and updates the error/status; retrying a failed message is the whole point, so
    /// there is no invalid-transition guard here.</summary>
    public void MarkFailed(string error)
    {
        AttemptCount++;
        Error = Truncate(error);
        Status = OutboxMessageStatus.Failed;
    }

    /// <summary>Reset a failed message back to Pending so the dispatcher will pick it up again.
    /// Keeps the historical <see cref="AttemptCount"/> — resetting it would hide how many times this
    /// message has genuinely failed.</summary>
    public void ResetForRetry()
    {
        if (Status != OutboxMessageStatus.Failed)
        {
            throw new InvalidOperationException($"Outbox message {Id} cannot be reset for retry from status '{Status}'.");
        }

        Status = OutboxMessageStatus.Pending;
        Error = null;
    }

    private static string Truncate(string value) =>
        value.Length > MaxErrorLength ? value[..MaxErrorLength] : value;
}
