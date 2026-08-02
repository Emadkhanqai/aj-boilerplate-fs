using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Domain.Messaging.Inbox;

/// <summary>
/// The idempotency record for an inbound integration event. <see cref="SourceEventId"/> is the
/// originating system's own event identifier; a consumer looks one up by it before processing to
/// guarantee at-most-once handling of a message that may be redelivered. Persistence-ignorant — no
/// EF concerns here.
/// </summary>
public sealed class InboxMessage
{
    private const int MaxErrorLength = 2000;

    public int Id { get; }

    public string SourceEventId { get; private set; }

    public string EventType { get; private set; }

    public string PayloadJson { get; private set; }

    public DateTime ReceivedAtUtc { get; private set; }

    public DateTime? ProcessedAtUtc { get; private set; }

    public InboxMessageStatus Status { get; private set; }

    public string? Error { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>The originating event's schema version (mirrors <see cref="OutboxMessage.EventVersion"/>)
    /// — lets a future consumer branch on payload shape without a separate migration.</summary>
    public int EventVersion { get; private set; } = 1;

    /// <summary>The correlation id of the request that delivered this event (mirrors
    /// <see cref="OutboxMessage.CorrelationId"/>), so this inbox row — and the audit entries any
    /// resulting domain mutation appends — can be joined back to the originating call without
    /// deserializing <see cref="PayloadJson"/>.</summary>
    public string? CorrelationId { get; private set; }

    private InboxMessage()
    {
        SourceEventId = EventType = PayloadJson = string.Empty;
    }

    private InboxMessage(string sourceEventId, string eventType, string payloadJson, DateTime receivedAtUtc,
        int eventVersion, string? correlationId)
    {
        SourceEventId = sourceEventId;
        EventType = eventType;
        PayloadJson = payloadJson;
        ReceivedAtUtc = receivedAtUtc;
        Status = InboxMessageStatus.Received;
        AttemptCount = 0;
        ProcessedAtUtc = null;
        EventVersion = eventVersion;
        CorrelationId = correlationId;
    }

    public static InboxMessage Create(string sourceEventId, string eventType, string payloadJson, DateTime receivedAtUtc,
        int eventVersion = 1, string? correlationId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId))
        {
            throw new DomainException("An inbound event's source event id is required.");
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new DomainException("An inbound event's event type is required.");
        }

        return new InboxMessage(sourceEventId, eventType, payloadJson, receivedAtUtc, eventVersion, correlationId);
    }

    /// <summary>Mark this inbound event successfully processed. Throws if already processed —
    /// calling this twice is a bug in the consumer, not a business-rule violation, hence
    /// <see cref="InvalidOperationException"/> rather than a domain exception.</summary>
    public void MarkProcessed(DateTime nowUtc)
    {
        if (Status == InboxMessageStatus.Processed)
        {
            throw new InvalidOperationException($"Inbox message {Id} has already been processed.");
        }

        Status = InboxMessageStatus.Processed;
        ProcessedAtUtc = nowUtc;
        Error = null;
    }

    /// <summary>Record a failed processing attempt. Safe to call repeatedly — each call increments
    /// the attempt count and updates the error/status.</summary>
    public void MarkFailed(string error)
    {
        AttemptCount++;
        Error = Truncate(error);
        Status = InboxMessageStatus.Failed;
    }

    private static string Truncate(string value) =>
        value.Length > MaxErrorLength ? value[..MaxErrorLength] : value;
}
