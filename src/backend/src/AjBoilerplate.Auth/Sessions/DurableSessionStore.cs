using Microsoft.Extensions.Logging;
using AjBoilerplate.Auth.Abstractions;

namespace AjBoilerplate.Auth.Sessions;

public sealed partial class DurableSessionStore : ISessionStore
{
    public const string CacheServiceKey = "session-cache";

    private readonly ISessionStore _database;
    private readonly ISessionStore _cache;
    private readonly ILogger<DurableSessionStore> _logger;

    public DurableSessionStore(ISessionStore database, ISessionStore cache, ILogger<DurableSessionStore> logger)
    {
        _database = database;
        _cache = cache;
        _logger = logger;
    }

    public required TimeSpan IdleTimeout { get; init; }

    public async Task<AuthSession?> GetAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync(sessionKey, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var stored = await _database.GetAsync(sessionKey, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        LogCacheMiss(_logger);
        await _cache.CreateAsync(sessionKey, stored, IdleTimeout, cancellationToken);
        return stored;
    }

    public async Task CreateAsync(string sessionKey, AuthSession session, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        await _database.CreateAsync(sessionKey, session, idleTimeout, cancellationToken);
        await _cache.CreateAsync(sessionKey, session, idleTimeout, cancellationToken);
    }

    public async Task TouchAsync(string sessionKey, string subject, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        await _database.TouchAsync(sessionKey, subject, idleTimeout, cancellationToken);
        await _cache.TouchAsync(sessionKey, subject, idleTimeout, cancellationToken);
    }

    public async Task UpdateTokensAsync(string sessionKey, string jwt, DateTimeOffset jwtExpiresAt, string? refreshToken, CancellationToken cancellationToken)
    {
        await _database.UpdateTokensAsync(sessionKey, jwt, jwtExpiresAt, refreshToken, cancellationToken);
        await _cache.UpdateTokensAsync(sessionKey, jwt, jwtExpiresAt, refreshToken, cancellationToken);
    }

    public async Task UpdateRefreshTokenAsync(string sessionKey, string refreshToken, CancellationToken cancellationToken)
    {
        await _database.UpdateRefreshTokenAsync(sessionKey, refreshToken, cancellationToken);
        await _cache.UpdateRefreshTokenAsync(sessionKey, refreshToken, cancellationToken);
    }

    public async Task<bool> MarkForceRefreshAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var existed = await _database.MarkForceRefreshAsync(sessionKey, cancellationToken);

        await _cache.MarkForceRefreshAsync(sessionKey, cancellationToken);
        return existed;
    }

    public async Task StampSignInTimeAsync(string sessionKey, DateTimeOffset signedInAt, CancellationToken cancellationToken)
    {
        await _database.StampSignInTimeAsync(sessionKey, signedInAt, cancellationToken);
        await _cache.StampSignInTimeAsync(sessionKey, signedInAt, cancellationToken);
    }

    public async Task DeleteAsync(string sessionKey, string subject, CancellationToken cancellationToken)
    {
        await _database.DeleteAsync(sessionKey, subject, cancellationToken);
        await _cache.DeleteAsync(sessionKey, subject, cancellationToken);
    }

    public async Task<bool> EndAsync(string sessionKey, string subject, SessionEnd ending, CancellationToken cancellationToken)
    {
        var ended = await _database.EndAsync(sessionKey, subject, ending, cancellationToken);
        await _cache.DeleteAsync(sessionKey, subject, cancellationToken);
        return ended;
    }

    public Task<IReadOnlyCollection<string>> GetSessionKeysForSubjectAsync(string subject, CancellationToken cancellationToken) =>
        _database.GetSessionKeysForSubjectAsync(subject, cancellationToken);

    public Task RemoveFromSubjectIndexAsync(string subject, string sessionKey, CancellationToken cancellationToken) =>
        _cache.RemoveFromSubjectIndexAsync(subject, sessionKey, cancellationToken);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session cache miss; served from the database.")]
    private static partial void LogCacheMiss(ILogger logger);
}
