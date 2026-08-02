using AjBoilerplate.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AjBoilerplate.Infrastructure.Email;

/// <summary>
/// The fallback email transport used when no SMTP host is configured (local dev, tests, offline): it
/// records that the message is ready to send instead of delivering it. Deliberately logs only the
/// recipient, subject, and attachment size — never the body, which routinely carries one-time codes
/// and signed links.
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendAsync(string to, string subject, string body, EmailAttachment? attachment, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Email to {To} '{Subject}' ({Bytes} bytes attached) ready to send (no SMTP host configured).",
                to, subject, attachment?.Content.Length ?? 0);
        }

        return Task.CompletedTask;
    }
}
