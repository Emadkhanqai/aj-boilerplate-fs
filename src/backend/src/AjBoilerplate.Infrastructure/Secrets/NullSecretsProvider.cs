using AjBoilerplate.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AjBoilerplate.Infrastructure.Secrets;

/// <summary>
/// The fallback secrets provider used when the selected cloud's secret store is not configured
/// (local dev, tests, offline) — the same conditional-registration shape as
/// <see cref="Messaging.LoggingIntegrationEventPublisher"/> and
/// <see cref="Email.LoggingEmailSender"/>.
///
/// It returns null for every lookup, which <see cref="ISecretsProvider"/> already defines as "no
/// such secret", so a caller behaves exactly as it would against a real-but-empty store. It logs
/// each miss at Debug rather than staying silent, so "why is this integration disabled locally?" is
/// answerable without attaching a debugger. It never invents or caches a value: a boilerplate that
/// quietly substituted a fake secret would be a security defect, not a convenience.
/// </summary>
public sealed class NullSecretsProvider : ISecretsProvider
{
    private readonly ILogger<NullSecretsProvider> _logger;

    public NullSecretsProvider(ILogger<NullSecretsProvider> logger) => _logger = logger;

    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Secret '{SecretName}' requested, but no cloud secret store is configured.", name);
        return Task.FromResult<string?>(null);
    }
}
