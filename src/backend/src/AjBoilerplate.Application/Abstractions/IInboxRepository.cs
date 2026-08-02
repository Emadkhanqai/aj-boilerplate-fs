using AjBoilerplate.Domain.Messaging.Inbox;

namespace AjBoilerplate.Application.Abstractions;

/// <summary>Port for the idempotency store of inbound integration events. Implemented in
/// Infrastructure.</summary>
public interface IInboxRepository
{
    /// <summary>The row for this originating system's event id, or null if it has never been seen
    /// before — the dedup lookup an inbound consumer must make before doing any business-logic
    /// work.</summary>
    Task<InboxMessage?> FindBySourceEventIdAsync(string sourceEventId, CancellationToken cancellationToken);

    Task AddAsync(InboxMessage message, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Detach every entity this repository's underlying persistence context is tracking. A SQL Server
    /// deadlock (error 1205) fully rolls back the current transaction, but does not by itself undo an
    /// EF Core change tracker's in-memory state — an entity added/modified during the failed attempt
    /// stays tracked as if it were still pending, and retrying without clearing that state first can
    /// make the retry insert a second, duplicate-keyed row alongside the still-tracked (never
    /// actually committed) one. Calling this before a retry forces the next attempt to re-query
    /// everything fresh, exactly as if it were a brand-new call. A no-op for a non-EF-backed
    /// implementation (e.g. an in-memory test fake), which has no such tracking concept.
    /// </summary>
    void ClearChangeTracking();
}
