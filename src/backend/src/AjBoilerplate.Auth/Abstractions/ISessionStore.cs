using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Abstractions;

/// <summary>
/// Session persistence. Keys passed here are <see cref="Security.SessionIds.StorageKey"/> hashes,
/// never the raw cookie value, so a dump of the store cannot be replayed as cookies.
/// </summary>
public interface ISessionStore
{
    Task<AuthSession?> GetAsync(string sessionKey, CancellationToken cancellationToken);

    /// <summary>Creates the session with <paramref name="idleTimeout"/> TTL and indexes it under its subject.</summary>
    Task CreateAsync(string sessionKey, AuthSession session, TimeSpan idleTimeout, CancellationToken cancellationToken);

    /// <summary>Slides the idle TTL of the session (and of the subject index).</summary>
    Task TouchAsync(string sessionKey, string subject, TimeSpan idleTimeout, CancellationToken cancellationToken);

    /// <summary>Atomically replaces JWT, expiry and refresh token and clears <c>force_refresh</c>. No-op when the session is gone.</summary>
    Task UpdateTokensAsync(string sessionKey, string jwt, DateTimeOffset jwtExpiresAt, string? refreshToken, CancellationToken cancellationToken);

    /// <summary>Persists a rotated refresh token only (Keycloak failed after the identity provider rotated it).</summary>
    Task UpdateRefreshTokenAsync(string sessionKey, string refreshToken, CancellationToken cancellationToken);

    /// <summary>Sets <c>force_refresh</c>. Returns false when the session no longer exists.</summary>
    Task<bool> MarkForceRefreshAsync(string sessionKey, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="signedInAt"/> as the sign-in time ONLY if the session has none (no value,
    /// or one at or before the Unix epoch). No-op otherwise and when the session is gone.
    /// </summary>
    Task StampSignInTimeAsync(string sessionKey, DateTimeOffset signedInAt, CancellationToken cancellationToken);

    /// <summary>Deletes the session and removes it from its subject's index.</summary>
    /// <remarks>
    /// Housekeeping only. A session that ENDS (logout, expiry, a definitive verdict about the user) goes through
    /// <see cref="EndAsync"/>, which is what tells the portal. A delete reports nothing to anyone.
    /// </remarks>
    Task DeleteAsync(string sessionKey, string subject, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the session: deletes it as <see cref="DeleteAsync"/> does and, where the store keeps the session-ended outbox (SQL),
    /// queues the event for the session's portal IN THE SAME TRANSACTION as the delete.
    /// </summary>
    /// <returns>
    /// <c>true</c> when this call removed the session; <c>false</c> when it was already gone. Only a call that removed the row queues
    /// an event, so two paths ending one session concurrently (a logout racing the sweeper) produce one event, not two.
    /// </returns>
    Task<bool> EndAsync(string sessionKey, string subject, SessionEnd ending, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> GetSessionKeysForSubjectAsync(string subject, CancellationToken cancellationToken);

    Task RemoveFromSubjectIndexAsync(string subject, string sessionKey, CancellationToken cancellationToken);
}

/// <summary>
/// A short-lived record that a session was ended by a DEFINITIVE no-role verdict. Read by
/// <c>/auth/validate</c> when the cookie names a session that no longer exists, so a request racing
/// the one that ended it answers <c>403 X-Usp-Access: revoked</c> instead of the 401 that would send
/// the browser to sign in.
/// </summary>
/// <remarks>
/// A cache, not a record: losing a mark costs a 401, never a sign-out and never a revoked answer.
/// Implementations therefore swallow their own transport failures, and a failed read answers false.
/// </remarks>
public interface IRevokedSessionMarks
{
    Task MarkAsync(string sessionKey, TimeSpan lifetime, CancellationToken cancellationToken);

    Task<bool> IsMarkedAsync(string sessionKey, CancellationToken cancellationToken);
}

/// <summary>Pending sign-in state (PKCE verifier + return path + browser binding).</summary>
public interface ILoginStateStore
{
    Task SaveAsync(string state, LoginState login, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Atomically reads and deletes: a state value is usable exactly once.</summary>
    Task<LoginState?> TakeAsync(string state, CancellationToken cancellationToken);
}

/// <summary>Portal hand-off tickets. Keys are SHA-256 hashes of the ticket value, never the value itself.</summary>
public interface IHandoffTicketStore
{
    Task SaveAsync(string ticketKey, HandoffTicket ticket, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Atomically reads and deletes: a ticket is usable exactly once.</summary>
    Task<HandoffTicket?> TakeAsync(string ticketKey, CancellationToken cancellationToken);
}

/// <summary>Distributed single-flight lock (<c>SET NX PX</c> + compare-and-delete).</summary>
public interface IDistributedLock
{
    /// <summary>Returns a handle when acquired, <c>null</c> when another holder has it.
    /// Disposing the handle releases the lock only if it is still ours.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken);
}
