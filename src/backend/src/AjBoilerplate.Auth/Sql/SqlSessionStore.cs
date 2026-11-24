using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Auth.Sql;

/// <summary>The durable session store. Tokens are encrypted before they reach a row.</summary>
internal sealed class SqlSessionStore : ISessionStore
{
    /// <summary>Touching rewrites the row, so a session seen again within this window is not written again.</summary>
    internal static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<AuthDbContext> _contexts;
    private readonly SessionTokenProtector _protector;
    private readonly TimeProvider _time;

    public SqlSessionStore(IDbContextFactory<AuthDbContext> contexts, SessionTokenProtector protector, TimeProvider time)
    {
        _contexts = contexts;
        _protector = protector;
        _time = time;
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
            row.CreatedAt);
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

    public Task DeleteAsync(string sessionKey, string subject, CancellationToken cancellationToken) =>
        EndAsync(sessionKey, subject, cancellationToken);

    public async Task<bool> EndAsync(string sessionKey, string subject, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.Where(x => x.SessionKey == sessionKey).ExecuteDeleteAsync(cancellationToken) > 0;
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

    /// <summary>Deletes up to <paramref name="batchSize"/> rows that are idle-expired or past the absolute lifetime. Returns how many.</summary>
    public async Task<int> DeleteExpiredAsync(int batchSize, TimeSpan absoluteLifetime, CancellationToken cancellationToken)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);

        var now = _time.GetUtcNow();
        var oldestAllowed = now - absoluteLifetime;
        var keys = await db.Sessions
            .AsNoTracking()
            .Where(x => x.ExpiresAt <= now || (x.CreatedAt > DateTimeOffset.UnixEpoch && x.CreatedAt <= oldestAllowed))
            .OrderBy(x => x.ExpiresAt)
            .Take(batchSize)
            .Select(x => x.SessionKey)
            .ToListAsync(cancellationToken);

        return keys.Count == 0
            ? 0
            : await db.Sessions.Where(x => keys.Contains(x.SessionKey)).ExecuteDeleteAsync(cancellationToken);
    }
}
