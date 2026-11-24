using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.SessionEvents;

/// <summary>
/// The loop around <see cref="SessionEndedDispatcher"/>: every poll interval, drain whatever is
/// due; about once an hour, purge settled events past retention.
/// </summary>
/// <remarks>
/// A failure here (SQL unreachable, say) is logged and retried on the next tick. It never touches
/// a request: sign-out, validation and sign-in do not wait on this loop and cannot see it fail.
/// </remarks>
internal sealed partial class SessionEndedDispatchService : BackgroundService
{
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    private readonly SessionEndedDispatcher _dispatcher;
    private readonly AuthOptions _options;
    private readonly PortalRegistry _portals;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionEndedDispatchService> _logger;

    public SessionEndedDispatchService(
        SessionEndedDispatcher dispatcher,
        IOptions<AuthOptions> options,
        PortalRegistry portals,
        TimeProvider time,
        ILogger<SessionEndedDispatchService> logger)
    {
        _dispatcher = dispatcher;
        _options = options.Value;
        _portals = portals;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Say at start-up which portals are told, and how the call authenticates, so "why did that portal get nothing" starts from
        // a log query. The receiver URL carries nothing secret; the key and any token never reach a log.
        var receivers = 0;
        foreach (var portal in _portals.All.Where(p => p.SessionEvents.IsConfigured))
        {
            receivers++;
            LogReceiver(_logger, portal.ClientId, portal.SessionEvents.Url, portal.SessionEvents.UsesKey ? "shared key" : "Google ID token");
        }

        var delivery = _options.SessionEndDelivery;
        if (receivers == 0)
        {
            // Feature off: nothing is queued for a portal with no receiver, so there is nothing to poll for. ONE pass still runs, so
            // an event queued while a receiver WAS configured is settled (abandoned and logged at Error) rather than stranded.
            LogNoReceivers(_logger);
            await RunPassAsync(delivery, stoppingToken);
            return;
        }

        using var timer = new PeriodicTimer(delivery.PollInterval, _time);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunPassAsync(delivery, stoppingToken);
            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                return;
            }
        }
    }

    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    /// <summary>One drain of whatever is due, and (at most hourly) a purge of settled events. A failure is logged, never thrown.</summary>
    private async Task RunPassAsync(SessionEndDeliveryOptions delivery, CancellationToken stoppingToken)
    {
        try
        {
            // Drain: keep claiming while a full batch came back, so a backlog after an outage clears at the send rate rather than
            // one batch per poll interval.
            while (await _dispatcher.DispatchDueAsync(stoppingToken) >= delivery.BatchSize && !stoppingToken.IsCancellationRequested)
            {
            }

            if (_time.GetUtcNow() - _lastPurge >= PurgeEvery)
            {
                _lastPurge = _time.GetUtcNow();
                var purged = await _dispatcher.PurgeSettledAsync(stoppingToken);
                if (purged > 0)
                {
                    LogPurged(_logger, purged);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPassFailed(_logger, ex, SessionEndedDispatcher.Surface, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session-ended events go to {Portal} at {ReceiverUrl}, authenticated with a {Authentication}.")]
    private static partial void LogReceiver(ILogger logger, string portal, string receiverUrl, string authentication);

    [LoggerMessage(Level = LogLevel.Information, Message = "No portal has a session-ended receiver configured: session ends are not queued for anyone, and the dispatcher makes one settling pass and stops.")]
    private static partial void LogNoReceivers(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} settled session-ended event(s) past retention.")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session-ended dispatch pass failed on {Surface} ({ExceptionType}); queued events are kept and retried on the next tick.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, string surface, string exceptionType);
}
