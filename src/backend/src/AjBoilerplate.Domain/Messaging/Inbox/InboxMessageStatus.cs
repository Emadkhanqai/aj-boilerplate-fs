namespace AjBoilerplate.Domain.Messaging.Inbox;

/// <summary>Processing state of an <see cref="InboxMessage"/> row.</summary>
public enum InboxMessageStatus
{
    Received,
    Processed,
    Failed,
}
