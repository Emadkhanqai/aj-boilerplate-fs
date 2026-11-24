using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Abstractions;

/// <summary>
/// The durable queue of session-ended events (docs/auth/session-ended-contract.md). Events are WRITTEN by
/// <see cref="ISessionStore.EndAsync"/>, in the same transaction as the delete that ends the session, so "the session ended" and
/// "the portal will be told" cannot disagree. This interface is the dispatcher's half: claim, then settle.
/// </summary>
public interface ISessionEndedOutbox
{
    /// <summary>
    /// Reserves up to <paramref name="max"/> due events for <paramref name="lease"/> and returns them, oldest first. An event is
    /// claimed by at most one caller at a time: two instances polling together never receive the same event inside one lease.
    /// </summary>
    Task<IReadOnlyList<SessionEndedEvent>> ClaimDueAsync(int max, TimeSpan lease, CancellationToken cancellationToken);

    Task MarkDeliveredAsync(Guid eventId, string outcome, CancellationToken cancellationToken);

    Task ScheduleRetryAsync(Guid eventId, DateTimeOffset nextAttemptAt, string outcome, CancellationToken cancellationToken);

    Task AbandonAsync(Guid eventId, string outcome, CancellationToken cancellationToken);

    /// <summary>Deletes delivered and abandoned events that settled before <paramref name="before"/>.</summary>
    Task<int> PurgeSettledAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary>Sends one event to one portal. Classifies; never throws for a delivery failure.</summary>
public interface ISessionEndedSender
{
    Task<SessionEndedDelivery> SendAsync(SessionEndedEvent sessionEnded, SessionEventsOptions target, CancellationToken cancellationToken);
}
