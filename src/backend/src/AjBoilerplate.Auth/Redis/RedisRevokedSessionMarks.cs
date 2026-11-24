using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using AjBoilerplate.Auth.Abstractions;

namespace AjBoilerplate.Auth.Redis;

internal sealed partial class RedisRevokedSessionMarks : IRevokedSessionMarks
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRevokedSessionMarks> _logger;

    public RedisRevokedSessionMarks(IConnectionMultiplexer redis, ILogger<RedisRevokedSessionMarks> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task MarkAsync(string sessionKey, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        try
        {
            await _redis.GetDatabase().StringSetAsync(RedisKeys.Revoked(sessionKey), "1", lifetime);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            LogUnavailable(_logger, "mark", ex);
        }
    }

    public async Task<bool> IsMarkedAsync(string sessionKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _redis.GetDatabase().KeyExistsAsync(RedisKeys.Revoked(sessionKey));
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            LogUnavailable(_logger, "read", ex);
            return false;
        }
    }

    private static bool IsTransport(Exception ex) =>
        ex is RedisException or RedisTimeoutException or ObjectDisposedException or TimeoutException;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revoked-session marks unavailable on {Operation}; a gone session answers 401.")]
    private static partial void LogUnavailable(ILogger logger, string operation, Exception exception);
}
