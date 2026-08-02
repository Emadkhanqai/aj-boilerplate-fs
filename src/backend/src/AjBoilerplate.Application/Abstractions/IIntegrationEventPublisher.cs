namespace AjBoilerplate.Application.Abstractions;

/// <summary>
/// Publishes one outbox message to the outbound integration channel. Implemented in Infrastructure —
/// a real transport once one exists, a logging no-op until then, the same dual real/logging pattern
/// as <see cref="IEmailSender"/>.
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>Publish <paramref name="payloadJson"/> as an event of type
    /// <paramref name="messageType"/>, carrying <paramref name="eventVersion"/> and
    /// <paramref name="correlationId"/> so a real transport can attach them as routable message
    /// attributes without a consumer needing to deserialize the body first. Throws if the publish
    /// fails — the caller catches broadly and records the failure on the outbox row rather than this
    /// method swallowing it.</summary>
    Task PublishAsync(string messageType, string payloadJson, int eventVersion, string? correlationId,
        CancellationToken cancellationToken);
}
