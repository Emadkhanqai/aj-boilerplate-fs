using System.Linq.Expressions;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Observability;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Auth.Sql;

/// <summary>The durable session store. Tokens are encrypted before they reach a row.</summary>
internal sealed partial class SqlSessionStore : ISessionStore
{
    /// <summary>The <c>CorrelationId</c> column's width; a longer id is not stored rather than cut.</summary>
    private const int MaxCorrelationIdLength = 64;

    /// <summary>Touching rewrites the row, so a session seen again within this window is not written again.</summary>
    internal static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<AuthDbContext> _contexts;
    private readonly SessionTokenProtector _protector;
    private readonly TimeProvider _time;
    private readonly PortalRegistry _portals;
    private readonly PortalSessionId _portalSessionIds;
    private readonly ILogger<SqlSessionStore> _logger;

    public SqlSessionStore(
        IDbContextFactory<AuthDbContext> contexts,
        SessionTokenProtector protector,
        TimeProvider time,
        PortalRegistry portals,
        PortalSessionId portalSessionIds,
        ILogger<SqlSessionStore> logger)
    {
        _contexts = contexts;
        _protector = protector;
        _time = time;
        _portals = portals;
        _portalSessionIds = portalSessionIds;
        _logger = logger;
    }

    public async Task<AuthSession?> GetAsync(string sessionKey, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        var now = _time.GetUtcNow();
        var row = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SessionKey == sessionKey && x.ExpiresAt > now, cancellationToken);

        // A row whose JWT cannot be decrypted (wrong key, tampered) is treated as no session, never as a fault.
        var jwt = row is null ? null : _protector.Unprotect(row.KeycloakJwt);
        if (row is null || string.IsNullOrEmpty(jwt))
        {
            return null;
        }

        return new AuthSession(
            jwt,
            row.JwtExpiresAt,
            _protector.Unprotect(row.EntraRefreshToken),
            row.Subject,
            row.ClientId,
            row.ForceRefresh,
            row.CreatedAt,
            row.RefreshVia);
    }

    public async Task CreateAsync(string sessionKey, AuthSession session, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Delete + insert in one transaction, inside the execution strategy that EnableRetryOnFailure requires.
        await using var forStrategy = await _contexts.CreateDbContextAsync(cancellationToken);
        var strategy = forStrategy.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(
            async ct =>
            {
                await using var db = await _contexts.CreateDbContextAsync(ct);
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                await db.Sessions.Where(x => x.SessionKey == sessionKey).ExecuteDeleteAsync(ct);

                var now = _time.GetUtcNow();
                db.Sessions.Add(new SessionRow
                {
                    SessionKey = sessionKey,
                    Subject = session.Subject,
                    ClientId = session.ClientId,
                    KeycloakJwt = _protector.Protect(session.KeycloakJwt)!,
                    JwtExpiresAt = session.JwtExpiresAt,
                    EntraRefreshToken = _protector.Protect(session.EntraRefreshToken),
                    ForceRefresh = session.ForceRefresh,
                    CreatedAt = session.CreatedAt,
                    RefreshVia = session.RefreshVia,
                    LastSeenAt = now,
                    ExpiresAt = now + idleTimeout,
                });

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            },
            cancellationToken);
    }

    public async Task TouchAsync(string sessionKey, string subject, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        var now = _time.GetUtcNow();

        var writeBefore = now - TouchInterval;
        await db.Sessions
            .Where(x => x.SessionKey == sessionKey && x.LastSeenAt < writeBefore)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LastSeenAt, now).SetProperty(x => x.ExpiresAt, now + idleTimeout),
                cancellationToken);
    }

    public async Task UpdateTokensAsync(string sessionKey, string jwt, DateTimeOffset jwtExpiresAt, string? refreshToken, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        var protectedJwt = _protector.Protect(jwt)!;
        var query = db.Sessions.Where(x => x.SessionKey == sessionKey);

        if (refreshToken is null)
        {
            await query.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.KeycloakJwt, protectedJwt)
                      .SetProperty(x => x.JwtExpiresAt, jwtExpiresAt)
                      .SetProperty(x => x.ForceRefresh, false),
                cancellationToken);
            return;
        }

        var protectedRefresh = _protector.Protect(refreshToken);
        await query.ExecuteUpdateAsync(
            s => s.SetProperty(x => x.KeycloakJwt, protectedJwt)
                  .SetProperty(x => x.JwtExpiresAt, jwtExpiresAt)
                  .SetProperty(x => x.EntraRefreshToken, protectedRefresh)
                  .SetProperty(x => x.ForceRefresh, false),
            cancellationToken);
    }

    public async Task UpdateRefreshTokenAsync(string sessionKey, string refreshToken, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        var protectedRefresh = _protector.Protect(refreshToken);
        await db.Sessions
            .Where(x => x.SessionKey == sessionKey)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EntraRefreshToken, protectedRefresh), cancellationToken);
    }

    public async Task<bool> MarkForceRefreshAsync(string sessionKey, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        var affected = await db.Sessions
            .Where(x => x.SessionKey == sessionKey)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ForceRefresh, true), cancellationToken);

        return affected > 0;
    }

    public async Task StampSignInTimeAsync(string sessionKey, DateTimeOffset signedInAt, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await db.Sessions
            .Where(x => x.SessionKey == sessionKey && x.CreatedAt <= DateTimeOffset.UnixEpoch)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, signedInAt), cancellationToken);
    }

    public async Task DeleteAsync(string sessionKey, string subject, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await db.Sessions.Where(x => x.SessionKey == sessionKey).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> GetSessionKeysForSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        var now = _time.GetUtcNow();
        return await db.Sessions
            .AsNoTracking()
            .Where(x => x.Subject == subject && x.ExpiresAt > now)
            .Select(x => x.SessionKey)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The SQL table needs no separate index: the subject lookup is the <c>IX_Sessions_Subject</c> index.</summary>
    public Task RemoveFromSubjectIndexAsync(string subject, string sessionKey, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<bool> EndAsync(string sessionKey, string subject, SessionEnd ending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ending);
        return EndCoreAsync(sessionKey, guard: null, _ => ending, cancellationToken);
    }

    /// <summary>
    /// Ends up to <paramref name="batchSize"/> sessions that are idle-expired or past the absolute lifetime, each with its
    /// session-ended event, and returns how many it ended.
    /// </summary>
    /// <remarks>
    /// <b>This is where most sessions end.</b> The idle timeout is shorter than the absolute lifetime, and a user who closes the
    /// browser never makes the request that would notice either, so their session is ended here, up to one sweep interval after its
    /// deadline. The event says when the deadline passed, not when the sweep ran. One short transaction per row, re-checking the
    /// deadline inside it, so a session touched between the read and the delete is not ended.
    /// </remarks>
    public async Task<int> EndExpiredAsync(int batchSize, TimeSpan absoluteLifetime, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var oldestAllowed = now - absoluteLifetime;
        Expression<Func<SessionRow, bool>> due = x =>
            x.ExpiresAt <= now || (x.CreatedAt > DateTimeOffset.UnixEpoch && x.CreatedAt <= oldestAllowed);

        List<string> keys;
        await using (var db = await _contexts.CreateDbContextAsync(cancellationToken))
        {
            keys = await db.Sessions
                .AsNoTracking()
                .Where(due)
                .OrderBy(x => x.ExpiresAt)
                .Take(batchSize)
                .Select(x => x.SessionKey)
                .ToListAsync(cancellationToken);
        }

        var ended = 0;
        foreach (var key in keys)
        {
            if (await EndCoreAsync(key, due, row => ExpiredEnd(row, absoluteLifetime), cancellationToken))
            {
                ended++;
            }
        }

        return ended;
    }

    /// <summary>
    /// Which deadline a swept session passed first. A recorded sign-in time whose absolute bound fell at or before the idle deadline
    /// means the absolute lifetime ended it; otherwise the idle timeout did.
    /// </summary>
    internal static SessionEnd ExpiredEnd(EndingSession row, TimeSpan absoluteLifetime)
    {
        if (row.CreatedAt > DateTimeOffset.UnixEpoch)
        {
            var absoluteDeadline = row.CreatedAt + absoluteLifetime;
            if (absoluteDeadline <= row.ExpiresAt)
            {
                return SessionEnd.Expired(SessionEndDetails.AbsoluteLifetime, absoluteDeadline);
            }
        }

        return SessionEnd.Expired(SessionEndDetails.IdleTimeout, row.ExpiresAt);
    }

    /// <summary>
    /// Delete the row and, when its portal has a session-ended receiver, queue the event: one transaction, owned by the execution
    /// strategy (which refuses to run inside a transaction it did not open, so begin, delete, insert and commit are one delegate).
    /// </summary>
    /// <remarks>
    /// The delete's ROW COUNT, not the earlier read, decides whether this call ended the session. Two transactions that both read
    /// the row both issue the DELETE; the second waits on the first's lock and then removes nothing, so it queues nothing. A
    /// strategy retry after a commit whose acknowledgement was lost finds the row gone and returns false, which is why the event
    /// cannot be queued twice that way either.
    /// </remarks>
    private async Task<bool> EndCoreAsync(
        string sessionKey,
        Expression<Func<SessionRow, bool>>? guard,
        Func<EndingSession, SessionEnd> decide,
        CancellationToken cancellationToken)
    {
        await using var forStrategy = await _contexts.CreateDbContextAsync(cancellationToken);
        var strategy = forStrategy.Database.CreateExecutionStrategy();

        var queued = await strategy.ExecuteAsync(
            async ct =>
            {
                await using var db = await _contexts.CreateDbContextAsync(ct);
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                var rows = db.Sessions.Where(x => x.SessionKey == sessionKey);
                if (guard is not null)
                {
                    rows = rows.Where(guard);
                }

                var row = await rows
                    .AsNoTracking()
                    .Select(x => new EndingSession(x.Subject, x.ClientId, x.CreatedAt, x.ExpiresAt))
                    .FirstOrDefaultAsync(ct);
                if (row is null || await rows.ExecuteDeleteAsync(ct) == 0)
                {
                    return (Ended: false, Event: (SessionEndedEventRow?)null);
                }

                SessionEndedEventRow? queuedEvent = null;
                var portal = _portals.FindByClientId(row.ClientId);
                if (portal is not null && portal.SessionEvents.IsConfigured)
                {
                    var end = decide(row);
                    var now = _time.GetUtcNow();
                    var correlation = RequestCorrelation.Current;
                    queuedEvent = new SessionEndedEventRow
                    {
                        EventId = Guid.CreateVersion7(now),

                        // From the key this call was GIVEN: derived from the cookie on a request path, and read back from the
                        // fixed-width column on the sweeper's, so both give the id the portal saw on X-Usp-Session-Id.
                        SessionId = _portalSessionIds.For(sessionKey),
                        ClientId = row.ClientId,
                        Reason = SessionEndReasons.ToWire(end.Reason),
                        Detail = end.Detail,
                        OccurredAt = end.OccurredAt,
                        SessionStartedAt = row.CreatedAt > DateTimeOffset.UnixEpoch ? row.CreatedAt : null,
                        CorrelationId = correlation is { Length: <= MaxCorrelationIdLength } ? correlation : null,
                        EnqueuedAt = now,
                        Attempts = 0,
                        NextAttemptAt = now,
                    };
                    db.SessionEndedOutbox.Add(queuedEvent);
                    await db.SaveChangesAsync(ct);
                }

                await tx.CommitAsync(ct);
                return (Ended: true, Event: queuedEvent);
            },
            cancellationToken);

        if (queued.Event is { } e)
        {
            LogQueued(_logger, e.EventId, e.ClientId, e.Reason, e.Detail);
        }

        return queued.Ended;
    }

    /// <summary>What ending a session needs to know about it, read inside the ending transaction.</summary>
    internal sealed record EndingSession(string Subject, string ClientId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    // EventId is the join key to the dispatcher's lines and to the portal's record. The session id is deliberately NOT logged.
    [LoggerMessage(Level = LogLevel.Information, Message = "Session ended ({Reason}, {Detail}); session-ended event {EventId} queued for portal {Portal}.")]
    private static partial void LogQueued(ILogger logger, Guid eventId, string portal, string reason, string detail);
}
