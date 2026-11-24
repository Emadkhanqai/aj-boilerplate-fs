using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;

namespace AjBoilerplate.Auth.Sessions;

/// <summary>
/// Drains the session-ended outbox to the portals (docs/auth/session-ended-contract.md): claim due events, send each, settle each.
/// </summary>
/// <remarks>
/// <para>
/// <b>At least once.</b> An event is marked delivered only after the portal's 2xx, so a crash
/// between the two re-sends it after the claim lease; the portal deduplicates on
/// <c>eventId</c>. Nothing here can make a user's logout wait or fail: the logout's only work is
/// the transaction that deletes the session and queues the event.
/// </para>
/// <para>
/// <b>Every line names the surface as a PARAMETER</b> (<see cref="Surface"/>), never as template
/// text, and carries the HTTP status and the outcome slug separately, so "which portal, which hop,
/// what answered" is a query, not a reading of prose. The
/// session id and the subject are never logged; the event id is the join key, and the portal holds
/// it too.
/// </para>
/// </remarks>
public sealed partial class SessionEndedDispatcher
{
    /// <summary>The receiver surface, logged as a field on every line.</summary>
    public const string Surface = "session-ended";

    private readonly ISessionEndedOutbox _outbox;
    private readonly ISessionEndedSender _sender;
    private readonly PortalRegistry _portals;
    private readonly SessionEndDeliveryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionEndedDispatcher> _logger;

    public SessionEndedDispatcher(
        ISessionEndedOutbox outbox,
        ISessionEndedSender sender,
        PortalRegistry portals,
        IOptions<AuthOptions> options,
        TimeProvider time,
        ILogger<SessionEndedDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _outbox = outbox;
        _sender = sender;
        _portals = portals;
        _options = options.Value.SessionEndDelivery;
        _time = time;
        _logger = logger;
    }

    /// <summary>Claims one batch of due events and settles every one of them. Returns the batch size.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var batch = await _outbox.ClaimDueAsync(_options.BatchSize, _options.ClaimLease, cancellationToken);
        foreach (var sessionEnded in batch)
        {
            await DispatchOneAsync(sessionEnded, cancellationToken);
        }

        return batch.Count;
    }

    /// <summary>Deletes settled events older than the retention window.</summary>
    public Task<int> PurgeSettledAsync(CancellationToken cancellationToken) =>
        _outbox.PurgeSettledAsync(_time.GetUtcNow() - _options.Retention, cancellationToken);

    private async Task DispatchOneAsync(SessionEndedEvent sessionEnded, CancellationToken cancellationToken)
    {
        // Resolved at SEND time, not queue time: a receiver URL corrected after the event was queued
        // is the one used, and a receiver removed since is reported rather than silently retried.
        var portal = _portals.FindByClientId(sessionEnded.ClientId);
        var portalName = sessionEnded.ClientId;
        if (portal is null || !portal.SessionEvents.IsConfigured)
        {
            await _outbox.AbandonAsync(sessionEnded.EventId, "no_receiver", cancellationToken);
            LogAbandoned(_logger, sessionEnded.EventId, portalName, Surface, sessionEnded.Attempt, null, "no_receiver", "no_receiver");
            return;
        }

        SessionEndedDelivery delivery;
        try
        {
            delivery = await _sender.SendAsync(sessionEnded, portal.SessionEvents, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sender is supposed to classify, not throw. If one throws anyway, that is our defect,
            // not the portal's, and the event is kept for the next attempt rather than lost.
            LogSenderFaulted(_logger, ex, sessionEnded.EventId, portalName, Surface, ex.GetType().Name);
            delivery = new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, null, "sender_fault");
        }

        switch (delivery.Kind)
        {
            case SessionEndedDeliveryKind.Delivered:
                await _outbox.MarkDeliveredAsync(sessionEnded.EventId, delivery.Outcome, cancellationToken);
                LogDelivered(_logger, sessionEnded.EventId, portalName, Surface, sessionEnded.Attempt, delivery.Status, delivery.Outcome);
                return;

            case SessionEndedDeliveryKind.Permanent:
                await _outbox.AbandonAsync(sessionEnded.EventId, delivery.Outcome, cancellationToken);
                LogAbandoned(_logger, sessionEnded.EventId, portalName, Surface, sessionEnded.Attempt, delivery.Status, delivery.Outcome, "refused_by_receiver");
                return;
        }

        var now = _time.GetUtcNow();
        if (now - sessionEnded.EnqueuedAt >= _options.MaxAge)
        {
            await _outbox.AbandonAsync(sessionEnded.EventId, delivery.Outcome, cancellationToken);
            LogAbandoned(_logger, sessionEnded.EventId, portalName, Surface, sessionEnded.Attempt, delivery.Status, delivery.Outcome, "max_age");
            return;
        }

        var delay = Backoff(sessionEnded.Attempt, _options.InitialRetryDelay, _options.MaxRetryDelay);
        await _outbox.ScheduleRetryAsync(sessionEnded.EventId, now + delay, delivery.Outcome, cancellationToken);
        LogRetrying(_logger, sessionEnded.EventId, portalName, Surface, sessionEnded.Attempt, delivery.Status, delivery.Outcome, (long)delay.TotalSeconds);
    }

    /// <summary>
    /// The wait after attempt <paramref name="attempt"/> failed: <paramref name="initial"/>,
    /// doubling per attempt, never more than <paramref name="max"/>.
    /// </summary>
    internal static TimeSpan Backoff(int attempt, TimeSpan initial, TimeSpan max)
    {
        var delay = initial;
        for (var i = 1; i < attempt && delay < max; i++)
        {
            delay += delay;
        }

        return delay < max ? delay : max;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Session-ended event {EventId} delivered to {Portal} on {Surface}: {Outcome} (HTTP {Status}, attempt {Attempt}).")]
    private static partial void LogDelivered(ILogger logger, Guid eventId, string portal, string surface, int attempt, int? status, string outcome);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Session-ended event {EventId} to {Portal} on {Surface} not delivered: {Outcome} (HTTP {Status}, attempt {Attempt}); next attempt in {RetryInSeconds}s.")]
    private static partial void LogRetrying(ILogger logger, Guid eventId, string portal, string surface, int attempt, int? status, string outcome, long retryInSeconds);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Session-ended event {EventId} to {Portal} on {Surface} ABANDONED after {Attempt} attempt(s): {Outcome} (HTTP {Status}), because {AbandonReason}. The portal's history will not show this session's end.")]
    private static partial void LogAbandoned(ILogger logger, Guid eventId, string portal, string surface, int attempt, int? status, string outcome, string abandonReason);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Session-ended sender threw ({ExceptionType}) for event {EventId} to {Portal} on {Surface}; kept for retry.")]
    private static partial void LogSenderFaulted(ILogger logger, Exception exception, Guid eventId, string portal, string surface, string exceptionType);
}
