using AjBoilerplate.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AjBoilerplate.Infrastructure.Messaging;

/// <summary>
/// The default transport: there is no message broker wired up in a fresh boilerplate, so this
/// records that an outbox message is ready to send instead of delivering it — the same fallback role
/// <see cref="Email.LoggingEmailSender"/> plays for SMTP. Swap in a real transport behind
/// <see cref="IIntegrationEventPublisher"/> once one exists; <c>OutboxDispatcher</c> does not change.
/// </summary>
public sealed class LoggingIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly ILogger<LoggingIntegrationEventPublisher> _logger;

    public LoggingIntegrationEventPublisher(ILogger<LoggingIntegrationEventPublisher> logger) => _logger = logger;

    public Task PublishAsync(string messageType, string payloadJson, int eventVersion, string? correlationId,
        CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Integration event '{MessageType}' v{EventVersion} (correlation {CorrelationId}) ready to publish " +
                "(no transport configured): {Payload}",
                messageType, eventVersion, correlationId ?? "-", payloadJson);
        }

        return Task.CompletedTask;
    }
}
