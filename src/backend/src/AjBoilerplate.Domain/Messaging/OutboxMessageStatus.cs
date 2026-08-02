namespace AjBoilerplate.Domain.Messaging;

/// <summary>Dispatch state of an <see cref="OutboxMessage"/> row.</summary>
public enum OutboxMessageStatus
{
    Pending,
    Dispatched,
    Failed,
}
