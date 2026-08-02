using AjBoilerplate.Application.Messaging;

namespace AjBoilerplate.Api.Messaging;

/// <summary>
/// Drives the transactional-outbox dispatcher on a fixed timer. <see cref="OutboxDispatcher"/>
/// (Application layer) does the actual publish-a-batch work.
///
/// <see cref="IOutboxDispatcher"/> — and everything behind it: the repository, its DbContext — is
/// scoped, but a <see cref="BackgroundService"/> is itself a singleton, so a fresh
/// <see cref="IServiceScope"/> is created per tick. A single tick's exception (a transient database
/// blip, say) is caught and logged rather than allowed to crash the hosted service: an unhandled
/// exception here silently stops the outbox draining for the process's whole remaining lifetime.
/// </summary>
public sealed class OutboxDispatcherHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherHostedService> _logger;

    public OutboxDispatcherHostedService(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcherHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        // Dispatch once immediately on startup, then every PollInterval — otherwise the first batch
        // would sit un-dispatched for a full interval after the app starts.
        await DispatchOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await DispatchOnceAsync(stoppingToken);
        }
    }

    private async Task DispatchOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
            var dispatched = await dispatcher.DispatchPendingAsync(stoppingToken);

            if (dispatched > 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Outbox dispatcher tick attempted {Count} message(s).", dispatched);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — not a tick failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Outbox dispatcher tick failed; will retry on the next interval.");
        }
    }
}
