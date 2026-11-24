using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Redis;

internal sealed partial class CacheSessionStore : ISessionStore
{
    private readonly RedisSessionStore _inner;
    private readonly ILogger<CacheSessionStore> _logger;

    public CacheSessionStore(RedisSessionStore inner, ILogger<CacheSessionStore> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task<AuthSession?> GetAsync(string sessionKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.GetAsync(sessionKey, cancellationToken);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            LogUnavailable(_logger, "get", ex);
            return null;
        }
    }

    public Task CreateAsync(string sessionKey, AuthSession session, TimeSpan idleTimeout, CancellationToken cancellationToken) =>
        SwallowAsync("create", () => _inner.CreateAsync(sessionKey, session, idleTimeout, cancellationToken));

    public Task TouchAsync(string sessionKey, string subject, TimeSpan idleTimeout, CancellationToken cancellationToken) =>
        SwallowAsync("touch", () => _inner.TouchAsync(sessionKey, subject, idleTimeout, cancellationToken));

    public Task UpdateTokensAsync(string sessionKey, string jwt, DateTimeOffset jwtExpiresAt, string? refreshToken, CancellationToken cancellationToken) =>
        SwallowAsync("update-tokens", () => _inner.UpdateTokensAsync(sessionKey, jwt, jwtExpiresAt, refreshToken, cancellationToken));

    public Task UpdateRefreshTokenAsync(string sessionKey, string refreshToken, CancellationToken cancellationToken) =>
        SwallowAsync("update-refresh", () => _inner.UpdateRefreshTokenAsync(sessionKey, refreshToken, cancellationToken));

    public async Task<bool> MarkForceRefreshAsync(string sessionKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.MarkForceRefreshAsync(sessionKey, cancellationToken);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            LogUnavailable(_logger, "force-refresh", ex);
            return false;
        }
    }

    public Task StampSignInTimeAsync(string sessionKey, DateTimeOffset signedInAt, CancellationToken cancellationToken) =>
        SwallowAsync("stamp-sign-in", () => _inner.StampSignInTimeAsync(sessionKey, signedInAt, cancellationToken));

    public Task DeleteAsync(string sessionKey, string subject, CancellationToken cancellationToken) =>
        SwallowAsync("delete", () => _inner.DeleteAsync(sessionKey, subject, cancellationToken));

    public async Task<bool> EndAsync(string sessionKey, string subject, CancellationToken cancellationToken)
    {
        await DeleteAsync(sessionKey, subject, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyCollection<string>> GetSessionKeysForSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.GetSessionKeysForSubjectAsync(subject, cancellationToken);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            LogUnavailable(_logger, "subject-index", ex);
            return [];
        }
    }

    public Task RemoveFromSubjectIndexAsync(string subject, string sessionKey, CancellationToken cancellationToken) =>
        SwallowAsync("index-remove", () => _inner.RemoveFromSubjectIndexAsync(subject, sessionKey, cancellationToken));

    private static bool IsTransport(Exception ex) =>
        ex is RedisException or RedisTimeoutException or ObjectDisposedException or TimeoutException;

    private async Task SwallowAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            LogUnavailable(_logger, operation, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session cache unavailable on {Operation}; continuing against the database.")]
    private static partial void LogUnavailable(ILogger logger, string operation, Exception exception);
}
